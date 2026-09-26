using System.Drawing;

namespace HitCam.Vision.Faces;

/// <summary>The two face models; the tracker only knows this, so tests can run it without models.</summary>
public interface IFaceModels : IDisposable
{
    /// <summary>"DirectML" or "CPU".</summary>
    string Provider { get; }

    IReadOnlyList<DetectedFace> Detect(VisionFrame frame);

    /// <summary>Unit-length fingerprint of a face (see <see cref="FaceRecognizer"/>).</summary>
    float[] Fingerprint(VisionFrame frame, DetectedFace face);
}

/// <summary>
/// A face followed across frames. <see cref="Box"/> is square, a little larger than the face (so a frame of delay
/// never uncovers it), normalized to the frame. <see cref="Hidden"/>: the effect goes on it. <see cref="Seen"/>:
/// found in this frame, false while it is held after being lost.
/// </summary>
public sealed record TrackedFace(int Id, RectangleF Box, bool Hidden, bool Seen);

public sealed record FaceTrackerOptions
{
    /// <summary>A detection continues a face track if they overlap at least this much.</summary>
    public float MatchIou { get; init; } = 0.3f;

    /// <summary>Weight of the new box in the moving average.</summary>
    public float Smoothing { get; init; } = 0.6f;

    /// <summary>A lost face keeps its place (and its effect) this long: a turned head is not uncovered.</summary>
    public TimeSpan Hold { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The square is this much larger than the face; a held face grows by <see cref="HeldGrowth"/> more.</summary>
    public float Margin { get; init; } = 1.15f;

    public float HeldGrowth { get; init; } = 1.2f;

    /// <summary>A seen face's fingerprint is refreshed this often (averaged, so recognition follows head turns).</summary>
    public TimeSpan Refresh { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>An uncovered face that stopped matching (and got hidden) is checked again this soon.</summary>
    public TimeSpan Recheck { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>
/// Follows faces, and remembers the people the user clicked: a new face is shown or hidden as
/// <see cref="HideNewFaces"/> says, unless its fingerprint matches someone clicked before, so a person who leaves and
/// comes back keeps their choice. The remembered people live only in memory. <see cref="Toggle"/> uncovers or hides a
/// face. Not thread-safe.
/// </summary>
public sealed class FaceTracker(IFaceModels models, FacePeople? people = null, FaceTrackerOptions? options = null)
{
    private readonly FaceTrackerOptions _options = options ?? new FaceTrackerOptions();
    private readonly List<State> _faces = [];
    // People the user clicked: shared with the engine, so they outlive this tracker (models reloaded).
    private readonly FacePeople _people = people ?? new FacePeople();
    private int _nextId = 1;
    private TimeSpan _lastUpdate = TimeSpan.MinValue;

    /// <summary>
    /// A face nobody clicked is hidden (privacy first) or shown (only the faces clicked are hidden; false detections,
    /// e.g. on hands, then cost nothing).
    /// </summary>
    public bool HideNewFaces { get; set; }

    /// <summary>People clicked so far (they stay until the app closes).</summary>
    public int RememberedPeople => _people.Count;

    /// <summary>Forgets the faces in the picture (camera switched); the remembered people are kept.</summary>
    public void Reset() => _faces.Clear();

    /// <summary>Forgets the remembered people too: every face is back to <see cref="HideNewFaces"/>.</summary>
    public void ForgetPeople()
    {
        _people.Clear();
        foreach (var face in _faces)
            face.Person = null;
    }

    /// <summary>Hides a shown face or uncovers a hidden one; the person is remembered unless that is the default again.</summary>
    public void Toggle(int id)
    {
        var face = _faces.FirstOrDefault(f => f.Id == id);
        if (face is null)
            return;
        var hide = !(face.Person?.Hidden ?? HideNewFaces);
        if (face.Person is { } person)
        {
            if (hide == HideNewFaces)
            {
                _people.Remove(person);
                foreach (var other in _faces.Where(f => f.Person == person))
                    other.Person = null;
            }
            else
            {
                _people.SetHidden(person, hide);
            }
        }
        else if (face.Fingerprint is { } fingerprint)
        {
            face.Person = _people.Add(fingerprint, hide);
        }
    }

    public IReadOnlyList<TrackedFace> Update(VisionFrame frame, TimeSpan now)
    {
        var detections = models.Detect(frame).ToList();

        // Greedy matching, best overlap first.
        var pairs = new List<(State Face, DetectedFace Detection, float Iou)>();
        foreach (var face in _faces)
        {
            foreach (var detection in detections)
            {
                var iou = Geometry.Iou(face.Raw, detection.Box);
                if (iou >= _options.MatchIou)
                    pairs.Add((face, detection, iou));
            }
        }
        var matchedFaces = new HashSet<State>();
        var matchedDetections = new HashSet<DetectedFace>();
        foreach (var (face, detection, _) in pairs.OrderByDescending(p => p.Iou))
        {
            if (matchedFaces.Contains(face) || matchedDetections.Contains(detection))
                continue;
            matchedFaces.Add(face);
            matchedDetections.Add(detection);
            // Found again after being held: whoever is there now may be someone else, so check at once.
            var wasHeld = face.LastSeen < _lastUpdate;
            face.Update(detection, now, _options.Smoothing);
            if (wasHeld || now - face.FingerprintTime >= _options.Refresh)
                Recognize(face, frame, detection, now);
        }

        _faces.RemoveAll(f => !matchedFaces.Contains(f) && now - f.LastSeen > _options.Hold);

        foreach (var detection in detections.Where(d => !matchedDetections.Contains(d)))
        {
            var face = new State(_nextId++, detection, now);
            _faces.Add(face);
            Recognize(face, frame, detection, now);
        }

        _lastUpdate = now;
        return [.. _faces.Select(f => f.ToTrackedFace(frame.Width, frame.Height, matchedFaces.Contains(f) || f.LastSeen == now, _options, HideNewFaces))];
    }

    /// <summary>Takes a fresh fingerprint: averaged into the face's (and its person's), and matched against the people.</summary>
    private void Recognize(State face, VisionFrame frame, DetectedFace detection, TimeSpan now)
    {
        float[] fresh;
        try
        {
            fresh = models.Fingerprint(frame, detection);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return;
        }
        face.FingerprintTime = now;
        // Someone else on this track (heads crossed, a stranger sat down where a held face was), or the same person
        // no longer recognizable (a turned head): the track's own fingerprint starts over.
        if (face.Fingerprint is { } own && FaceRecognizer.Similarity(own, fresh) < FaceRecognizer.SameFace)
            face.Fingerprint = null;
        face.Fingerprint = face.Fingerprint is null ? fresh : Average(face.Fingerprint, fresh);
        if (face.Person is { } person)
        {
            // A person's fingerprint only learns from faces that are them.
            if (FaceRecognizer.Similarity(person.Fingerprint, fresh) >= FaceRecognizer.SameFace)
            {
                person.Fingerprint = Average(person.Fingerprint, fresh);
                return;
            }
            // Not them now. Look again soon, not a refresh later: most likely a turned head.
            face.FingerprintTime = now - _options.Refresh + _options.Recheck;
            // Someone hidden on purpose stays hidden meanwhile (the safe side); someone uncovered is covered again,
            // or goes back to the default.
            if (person.Hidden)
                return;
            face.Person = null;
        }
        var best = _people.All.Select(p => (Person: p, Score: FaceRecognizer.Similarity(p.Fingerprint, fresh)))
            .Where(p => p.Score >= FaceRecognizer.SameFace)
            .OrderByDescending(p => p.Score)
            .FirstOrDefault();
        if (best.Person is not null)
            face.Person = best.Person;
        else if (face.Person is null && _people.AnyHidden)
            // Maybe someone hidden on purpose, not recognizable yet: shown by default meanwhile, so look again soon.
            face.FingerprintTime = now - _options.Refresh + _options.Recheck;
    }

    /// <summary>Running average of unit vectors, normalized again (older and newer weigh 3:1).</summary>
    private static float[] Average(float[] current, float[] fresh)
    {
        var result = new float[current.Length];
        var length = 0f;
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = current[i] * 0.75f + fresh[i] * 0.25f;
            length += result[i] * result[i];
        }
        length = MathF.Sqrt(length);
        if (length > 0)
        {
            for (var i = 0; i < result.Length; i++)
                result[i] /= length;
        }
        return result;
    }

    private sealed class State(int id, DetectedFace first, TimeSpan now)
    {
        public int Id { get; } = id;

        /// <summary>Smoothed box in frame pixels.</summary>
        public RectangleF Raw { get; private set; } = first.Box;

        public TimeSpan LastSeen { get; private set; } = now;

        public float[]? Fingerprint { get; set; }

        public TimeSpan FingerprintTime { get; set; } = TimeSpan.MinValue;

        /// <summary>The remembered person this face is; null: nobody clicked, the default applies.</summary>
        public FacePeople.Person? Person { get; set; }

        public void Update(DetectedFace detection, TimeSpan now, float smoothing)
        {
            var b = detection.Box;
            Raw = new RectangleF(
                Raw.X + (b.X - Raw.X) * smoothing, Raw.Y + (b.Y - Raw.Y) * smoothing,
                Raw.Width + (b.Width - Raw.Width) * smoothing, Raw.Height + (b.Height - Raw.Height) * smoothing);
            LastSeen = now;
        }

        public TrackedFace ToTrackedFace(int width, int height, bool seen, FaceTrackerOptions options, bool hideNewFaces)
        {
            var side = Math.Max(Raw.Width, Raw.Height) * options.Margin * (seen ? 1 : options.HeldGrowth);
            var cx = Raw.X + Raw.Width / 2;
            var cy = Raw.Y + Raw.Height / 2;
            var box = new RectangleF((cx - side / 2) / width, (cy - side / 2) / height, side / width, side / height);
            return new TrackedFace(Id, box, Person?.Hidden ?? hideNewFaces, seen);
        }
    }
}

/// <summary>
/// The people the user clicked (an averaged fingerprint each, hidden or uncovered); kept in memory only, until the app
/// closes. Changed on the tracker's thread; <see cref="AnyHidden"/> may be read from any thread.
/// </summary>
public sealed class FacePeople
{
    private readonly List<Person> _people = [];
    private volatile bool _anyHidden;

    public int Count => _people.Count;

    /// <summary>Someone was hidden on purpose: they must not show while the faces are not found yet.</summary>
    public bool AnyHidden => _anyHidden;

    internal IReadOnlyList<Person> All => _people;

    internal Person Add(float[] fingerprint, bool hidden)
    {
        var person = new Person(fingerprint) { Hidden = hidden };
        _people.Add(person);
        Recount();
        return person;
    }

    internal void Remove(Person person)
    {
        _people.Remove(person);
        Recount();
    }

    internal void SetHidden(Person person, bool hidden)
    {
        person.Hidden = hidden;
        Recount();
    }

    internal void Clear()
    {
        _people.Clear();
        Recount();
    }

    private void Recount() => _anyHidden = _people.Exists(p => p.Hidden);

    internal sealed class Person(float[] fingerprint)
    {
        public float[] Fingerprint { get; set; } = fingerprint;

        public bool Hidden { get; set; }
    }
}

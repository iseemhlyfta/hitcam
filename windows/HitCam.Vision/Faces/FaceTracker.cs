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
}

/// <summary>
/// Follows faces, and remembers the people the user uncovered: a new face is hidden unless its fingerprint matches
/// someone uncovered before, so a person who leaves and comes back keeps their choice. The remembered people live only
/// in memory. <see cref="Toggle"/> uncovers or hides a face. Not thread-safe.
/// </summary>
public sealed class FaceTracker(IFaceModels models, FacePeople? people = null, FaceTrackerOptions? options = null)
{
    private readonly FaceTrackerOptions _options = options ?? new FaceTrackerOptions();
    private readonly List<State> _faces = [];
    // People the user uncovered: shared with the engine, so they outlive this tracker (models reloaded).
    private readonly List<FacePeople.Person> _uncovered = (people ?? new FacePeople()).Uncovered;
    private int _nextId = 1;

    /// <summary>People uncovered so far (they stay until the app closes).</summary>
    public int UncoveredPeople => _uncovered.Count;

    /// <summary>Forgets the faces in the picture (camera switched); the uncovered people are kept.</summary>
    public void Reset() => _faces.Clear();

    /// <summary>Forgets the uncovered people too: every face is hidden again.</summary>
    public void ForgetPeople()
    {
        _uncovered.Clear();
        foreach (var face in _faces)
            face.Person = null;
    }

    /// <summary>Uncovers a hidden face (remembering the person) or hides an uncovered one (forgetting them).</summary>
    public void Toggle(int id)
    {
        var face = _faces.FirstOrDefault(f => f.Id == id);
        if (face is null)
            return;
        if (face.Person is { } person)
        {
            _uncovered.Remove(person);
            foreach (var other in _faces.Where(f => f.Person == person))
                other.Person = null;
        }
        else if (face.Fingerprint is { } fingerprint)
        {
            var uncovered = new FacePeople.Person(fingerprint);
            _uncovered.Add(uncovered);
            face.Person = uncovered;
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
            face.Update(detection, now, _options.Smoothing);
            if (now - face.FingerprintTime >= _options.Refresh)
                Recognize(face, frame, detection, now);
        }

        _faces.RemoveAll(f => !matchedFaces.Contains(f) && now - f.LastSeen > _options.Hold);

        foreach (var detection in detections.Where(d => !matchedDetections.Contains(d)))
        {
            var face = new State(_nextId++, detection, now);
            _faces.Add(face);
            Recognize(face, frame, detection, now);
        }

        return [.. _faces.Select(f => f.ToTrackedFace(frame.Width, frame.Height, matchedFaces.Contains(f) || f.LastSeen == now, _options))];
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
        face.Fingerprint = face.Fingerprint is null ? fresh : Average(face.Fingerprint, fresh);
        if (face.Person is { } person)
        {
            person.Fingerprint = Average(person.Fingerprint, fresh);
            return;
        }
        var best = _uncovered.Select(p => (Person: p, Score: FaceRecognizer.Similarity(p.Fingerprint, fresh)))
            .Where(p => p.Score >= FaceRecognizer.SameFace)
            .OrderByDescending(p => p.Score)
            .FirstOrDefault();
        if (best.Person is not null)
            face.Person = best.Person;
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

        /// <summary>The uncovered person this face is; null: hidden.</summary>
        public FacePeople.Person? Person { get; set; }

        public void Update(DetectedFace detection, TimeSpan now, float smoothing)
        {
            var b = detection.Box;
            Raw = new RectangleF(
                Raw.X + (b.X - Raw.X) * smoothing, Raw.Y + (b.Y - Raw.Y) * smoothing,
                Raw.Width + (b.Width - Raw.Width) * smoothing, Raw.Height + (b.Height - Raw.Height) * smoothing);
            LastSeen = now;
        }

        public TrackedFace ToTrackedFace(int width, int height, bool seen, FaceTrackerOptions options)
        {
            var side = Math.Max(Raw.Width, Raw.Height) * options.Margin * (seen ? 1 : options.HeldGrowth);
            var cx = Raw.X + Raw.Width / 2;
            var cy = Raw.Y + Raw.Height / 2;
            var box = new RectangleF((cx - side / 2) / width, (cy - side / 2) / height, side / width, side / height);
            return new TrackedFace(Id, box, Person is null, seen);
        }
    }
}

/// <summary>The people the user uncovered (an averaged fingerprint each); kept in memory only, until the app closes.</summary>
public sealed class FacePeople
{
    internal List<Person> Uncovered { get; } = [];

    public int Count => Uncovered.Count;

    internal sealed class Person(float[] fingerprint)
    {
        public float[] Fingerprint { get; set; } = fingerprint;
    }
}

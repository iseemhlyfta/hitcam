using System.Drawing;

namespace HitCam.Vision.Hands;

/// <summary>The two hand models; the tracker only knows this, so tests can run it without models.</summary>
public interface IHandModels : IDisposable
{
    /// <summary>"DirectML" or "CPU".</summary>
    string Provider { get; }

    /// <summary>Palms at least <paramref name="threshold"/> sure, best first, in pixels of the frame.</summary>
    IReadOnlyList<Palm> DetectPalms(VisionFrame frame, float threshold);

    HandLandmarks Landmarks(VisionFrame frame, HandRoi roi);
}

/// <summary>
/// A hand followed across frames. <see cref="Points"/> are the 21 landmarks (see <see cref="HandLandmarks"/>),
/// smoothed and normalized to the frame (0..1). <see cref="Id"/> stays the same while the hand is tracked.
/// <see cref="Pose"/> is set by <see cref="GestureDetector"/>.
/// </summary>
public sealed record TrackedHand(int Id, IReadOnlyList<PointF> Points, float Score, bool IsRight, HandPose Pose = HandPose.None);

public sealed record HandTrackerOptions
{
    public int MaxHands { get; init; } = 2;

    /// <summary>Palms less sure than this are not looked at.</summary>
    public float PalmThreshold { get; init; } = 0.5f;

    /// <summary>A new hand needs the landmark model to be at least this sure.</summary>
    public float NewHandScore { get; init; } = 0.7f;

    /// <summary>A tracked hand is lost when the score drops below this (lower than for a new hand: no flicker).</summary>
    public float KeepScore { get; init; } = 0.5f;

    /// <summary>Two tracked hands overlapping more than this are one hand seen twice; the older one stays.</summary>
    public float DuplicateIou { get; init; } = 0.5f;

    /// <summary>One Euro filter, as MediaPipe smooths hand landmarks: still hands stay still, fast ones do not lag.</summary>
    public float MinCutoff { get; init; } = 0.05f;

    public float Beta { get; init; } = 80f;

    public float DerivativeCutoff { get; init; } = 1f;
}

/// <summary>
/// MediaPipe-style hand tracking: the palm detector finds new hands; after that each hand is followed by running
/// the landmark model on a crop placed from its last landmarks, so the points stay on the fingers without detecting
/// again. Points are smoothed with a One Euro filter. Not thread-safe.
/// </summary>
public sealed class HandTracker(IHandModels models, HandTrackerOptions? options = null)
{
    private readonly HandTrackerOptions _options = options ?? new HandTrackerOptions();
    private List<State> _hands = [];
    private int _nextId = 1;

    public HandTrackerOptions Options => _options;

    public void Reset() => _hands.Clear();

    public IReadOnlyList<TrackedHand> Update(VisionFrame frame, TimeSpan now)
    {
        // Follow the known hands.
        var kept = new List<State>();
        foreach (var hand in _hands)
        {
            var landmarks = models.Landmarks(frame, hand.Roi);
            if (landmarks.Score < _options.KeepScore)
                continue;
            hand.Apply(landmarks, now);
            if (kept.Any(k => Geometry.Iou(k.Bounds, hand.Bounds) > _options.DuplicateIou))
                continue;
            kept.Add(hand);
        }
        _hands = kept;

        // Look for new ones while there is room.
        if (_hands.Count < _options.MaxHands)
        {
            foreach (var palm in models.DetectPalms(frame, _options.PalmThreshold))
            {
                if (_hands.Count >= _options.MaxHands)
                    break;
                if (_hands.Any(h => Covers(h.Bounds, palm.Box)))
                    continue;
                var landmarks = models.Landmarks(frame, HandGeometry.FromPalm(palm));
                if (landmarks.Score < _options.NewHandScore)
                    continue;
                var hand = new State(_nextId++, _options);
                hand.Apply(landmarks, now);
                if (_hands.Any(h => Geometry.Iou(h.Bounds, hand.Bounds) > _options.DuplicateIou))
                    continue;
                _hands.Add(hand);
            }
        }

        return [.. _hands.Select(h => h.ToTrackedHand(frame.Width, frame.Height))];
    }

    /// <summary>A palm inside a tracked hand is that hand, found again.</summary>
    private static bool Covers(RectangleF hand, RectangleF palm)
    {
        var center = new PointF(palm.X + palm.Width / 2, palm.Y + palm.Height / 2);
        return hand.Contains(center) || Geometry.Iou(hand, palm) > 0.3f;
    }

    private sealed class State(int id, HandTrackerOptions options)
    {
        private readonly OneEuroFilter[] _filters = [.. Enumerable.Range(0, HandLandmarker.PointCount * 2)
            .Select(_ => new OneEuroFilter(options.MinCutoff, options.Beta, options.DerivativeCutoff))];
        private readonly PointF[] _smoothed = new PointF[HandLandmarker.PointCount];

        public int Id { get; } = id;

        /// <summary>Where to look in the next frame.</summary>
        public HandRoi Roi { get; private set; }

        /// <summary>The box around the raw landmarks, pixels.</summary>
        public RectangleF Bounds { get; private set; }

        public float Score { get; private set; }

        public float Handedness { get; private set; }

        public void Apply(HandLandmarks landmarks, TimeSpan now)
        {
            var points = landmarks.Points;
            Roi = HandGeometry.FromLandmarks(points);
            Bounds = BoundsOf(points);
            Score = landmarks.Score;
            Handedness = landmarks.Handedness;
            // Speed is measured in hand sizes, so near and far hands are smoothed alike.
            var scale = (Bounds.Width + Bounds.Height) / 2;
            var valueScale = scale > 1 ? 1 / scale : 1;
            var seconds = now.TotalSeconds;
            for (var i = 0; i < points.Length; i++)
            {
                _smoothed[i] = new PointF(
                    _filters[i * 2].Apply(points[i].X, seconds, valueScale),
                    _filters[i * 2 + 1].Apply(points[i].Y, seconds, valueScale));
            }
        }

        public TrackedHand ToTrackedHand(int width, int height) => new(
            Id, [.. _smoothed.Select(p => new PointF(p.X / width, p.Y / height))], Score, Handedness >= 0.5f);

        private static RectangleF BoundsOf(ReadOnlySpan<PointF> points)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in points)
            {
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }
            return RectangleF.FromLTRB(minX, minY, maxX, maxY);
        }
    }
}

/// <summary>
/// The One Euro filter (Casiez et al., 2012), as in MediaPipe: a low-pass filter whose cutoff rises with speed, so
/// slow movements are smoothed hard (no jitter) and fast ones barely (no lag).
/// </summary>
public sealed class OneEuroFilter(float minCutoff, float beta, float derivativeCutoff)
{
    private bool _started;
    private double _lastTime;
    private float _value;
    private float _derivative;

    /// <param name="seconds">Time of the value; frames come at a varying rate.</param>
    /// <param name="valueScale">Multiplies the speed before it raises the cutoff (1 / object size).</param>
    public float Apply(float value, double seconds, float valueScale = 1)
    {
        if (!_started || seconds <= _lastTime)
        {
            // First value, or time went backwards (a reset clock): start over rather than divide by zero.
            var restart = !_started;
            _started = true;
            _lastTime = seconds;
            if (restart)
            {
                _value = value;
                _derivative = 0;
            }
            return _value;
        }
        var dt = (float)(seconds - _lastTime);
        _lastTime = seconds;
        var derivative = (value - _value) * valueScale / dt;
        _derivative += Alpha(derivativeCutoff, dt) * (derivative - _derivative);
        var cutoff = minCutoff + beta * MathF.Abs(_derivative);
        _value += Alpha(cutoff, dt) * (value - _value);
        return _value;
    }

    private static float Alpha(float cutoff, float dt)
    {
        var tau = 1 / (2 * MathF.PI * cutoff);
        return 1 / (1 + tau / dt);
    }
}

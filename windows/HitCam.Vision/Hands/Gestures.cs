using System.Drawing;

namespace HitCam.Vision.Hands;

/// <summary>What a hand shows.</summary>
public enum HandPose
{
    None,

    /// <summary>
    /// "Finger gun": the index finger straight (alone, or with the middle finger straight beside it) is the barrel,
    /// the thumb stands out, the ring and little fingers are curled.
    /// </summary>
    Gun,
}

/// <summary>
/// A shot of a finger gun. <see cref="Muzzle"/> is normalized to the frame (0..1); <see cref="Direction"/> is a unit
/// vector in frame pixels (y down) along the barrel; <see cref="Size"/> is the hand's size (wrist to the middle
/// finger's base) as a fraction of the frame height.
/// </summary>
public sealed record Shot(int HandId, PointF Muzzle, PointF Direction, float Size);

/// <summary>Recognizes hand poses from the 21 landmarks; works at any rotation and for either hand.</summary>
public static class PoseClassifier
{
    // Base, middle joint, last joint, tip of each finger.
    private static readonly int[] Thumb = [1, 2, 3, 4];
    private static readonly int[] Index = [5, 6, 7, 8];
    private static readonly int[] Middle = [9, 10, 11, 12];
    private static readonly int[] Ring = [13, 14, 15, 16];
    private static readonly int[] Little = [17, 18, 19, 20];

    /// <summary>The pose of one hand; <paramref name="points"/> in pixels (not normalized: angles need square pixels).</summary>
    public static HandPose Classify(IReadOnlyList<PointF> points)
    {
        if (points.Count < HandLandmarker.PointCount)
            return HandPose.None;
        var size = HandSize(points);
        if (size < 8)
            return HandPose.None;
        return IsGun(points, size) ? HandPose.Gun : HandPose.None;
    }

    /// <summary>Wrist to the middle finger's base: a measure of the hand that does not change with the fingers.</summary>
    public static float HandSize(IReadOnlyList<PointF> points) => Distance(points[0], points[9]);

    private static bool IsGun(IReadOnlyList<PointF> p, float size)
    {
        if (!IsExtended(p, Index) || !IsCurled(p, Ring) || !IsCurled(p, Little))
            return false;
        // The barrel: the index finger alone, or both fingers side by side pointing the same way (not a "V").
        var twoFingers = IsExtended(p, Middle);
        if (twoFingers && !IsTwoFingerBarrel(p, size))
            return false;
        if (!twoFingers && !IsCurled(p, Middle))
            return false;
        // The thumb stands away from the barrel, not folded over the other fingers.
        var thumbOut = Distance(p[4], p[5]) > 0.5f * size && Distance(p[4], p[6]) > 0.4f * size;
        return thumbOut && Angle(Sub(p[4], p[2]), Direction(p, Index)) > 25;
    }

    /// <summary>Index and middle fingertips together and the two fingers parallel.</summary>
    public static bool IsTwoFingerBarrel(IReadOnlyList<PointF> p, float size) =>
        Distance(p[8], p[12]) <= 0.45f * size && Angle(Direction(p, Index), Direction(p, Middle)) <= 30;

    /// <summary>Straight (the tip about as far from the base as the finger is long) and pointing away from the wrist.</summary>
    private static bool IsExtended(IReadOnlyList<PointF> p, int[] f) =>
        Straightness(p, f) > 0.8f && Distance(p[0], p[f[3]]) > Distance(p[0], p[f[1]]);

    /// <summary>Folded: the tip back towards the wrist, closer than the middle joint.</summary>
    private static bool IsCurled(IReadOnlyList<PointF> p, int[] f) =>
        Distance(p[0], p[f[3]]) < Distance(p[0], p[f[1]]) * 1.1f || Straightness(p, f) < 0.55f;

    private static float Straightness(IReadOnlyList<PointF> p, int[] f)
    {
        var length = Distance(p[f[0]], p[f[1]]) + Distance(p[f[1]], p[f[2]]) + Distance(p[f[2]], p[f[3]]);
        return length > 0 ? Distance(p[f[0]], p[f[3]]) / length : 0;
    }

    private static PointF Direction(IReadOnlyList<PointF> p, int[] f) => Sub(p[f[3]], p[f[0]]);

    internal static PointF Sub(PointF a, PointF b) => new(a.X - b.X, a.Y - b.Y);

    internal static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Angle between two vectors in degrees, 0..180.</summary>
    internal static float Angle(PointF a, PointF b)
    {
        var la = MathF.Sqrt(a.X * a.X + a.Y * a.Y);
        var lb = MathF.Sqrt(b.X * b.X + b.Y * b.Y);
        if (la == 0 || lb == 0)
            return 180;
        var cos = Math.Clamp((a.X * b.X + a.Y * b.Y) / (la * lb), -1f, 1f);
        return MathF.Acos(cos) * 180 / MathF.PI;
    }
}

public sealed record GestureOptions
{
    /// <summary>Frames in a row a pose must be seen before it counts (one-frame misreadings are ignored).</summary>
    public int ConfirmFrames { get; init; } = 2;

    /// <summary>A gun stays "held" this long after the pose is last seen: during the jerk the fingers blur.</summary>
    public TimeSpan Hold { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>How far back the jerk is looked for.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The barrel turning up by this many degrees within <see cref="Window"/> is a shot…</summary>
    public float RecoilDegrees { get; init; } = 22;

    /// <summary>…or its tip rising this much (in hand sizes) relative to the wrist, for a gun pointed at the camera.</summary>
    public float RecoilRise { get; init; } = 0.45f;

    /// <summary>No second shot from the same hand sooner than this.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMilliseconds(400);
}

/// <summary>
/// Finds poses and finger-gun shots in the tracked hands, frame by frame. A shot is a sharp upward jerk of the barrel
/// (recoil): the barrel turns up, or its tip rises relative to the wrist, fast. Moving the whole hand does not
/// count. Not thread-safe.
/// </summary>
public sealed class GestureDetector(GestureOptions? options = null)
{
    private readonly GestureOptions _options = options ?? new GestureOptions();
    private readonly Dictionary<int, State> _hands = [];

    public void Reset() => _hands.Clear();

    /// <summary>
    /// The hands with their <see cref="TrackedHand.Pose"/> set, and the shots fired in this frame.
    /// <paramref name="width"/> and <paramref name="height"/>: the frame size, to measure in square pixels.
    /// </summary>
    public (IReadOnlyList<TrackedHand> Hands, IReadOnlyList<Shot> Shots) Update(
        IReadOnlyList<TrackedHand> hands, int width, int height, TimeSpan now)
    {
        var result = new List<TrackedHand>(hands.Count);
        var shots = new List<Shot>();
        foreach (var hand in hands)
        {
            if (!_hands.TryGetValue(hand.Id, out var state))
                _hands[hand.Id] = state = new State();
            var points = hand.Points.Select(p => new PointF(p.X * width, p.Y * height)).ToArray();
            var pose = state.Pose(PoseClassifier.Classify(points), now, _options);
            result.Add(pose == hand.Pose ? hand : hand with { Pose = pose });
            if (state.Shoot(points, pose == HandPose.Gun, now, _options) is { } shot)
            {
                var size = PoseClassifier.HandSize(points);
                shots.Add(new Shot(hand.Id, new PointF(shot.Muzzle.X / width, shot.Muzzle.Y / height), shot.Direction, size / height));
            }
        }
        // Hands that are gone take their state with them.
        foreach (var id in _hands.Keys.Where(id => hands.All(h => h.Id != id)).ToList())
            _hands.Remove(id);
        return (result, shots);
    }

    private sealed class State
    {
        private readonly Queue<Sample> _history = new();
        private HandPose _candidate;
        private int _candidateFrames;
        private HandPose _pose;
        private TimeSpan _lastSeen;
        private TimeSpan? _lastShot;

        /// <summary>The confirmed pose, held for a while after it was last seen.</summary>
        public HandPose Pose(HandPose seen, TimeSpan now, GestureOptions options)
        {
            _candidateFrames = seen == _candidate ? _candidateFrames + 1 : 1;
            _candidate = seen;
            if (seen != HandPose.None && _candidateFrames >= options.ConfirmFrames)
            {
                _pose = seen;
                _lastSeen = now;
            }
            else if (_pose != HandPose.None && seen != _pose && now - _lastSeen > options.Hold)
            {
                _pose = HandPose.None;
            }
            else if (seen == _pose)
            {
                _lastSeen = now;
            }
            return _pose;
        }

        public (PointF Muzzle, PointF Direction)? Shoot(PointF[] p, bool gun, TimeSpan now, GestureOptions options)
        {
            var size = PoseClassifier.HandSize(p);
            if (!gun || size <= 0)
            {
                _history.Clear();
                return null;
            }
            var twoFingers = PoseClassifier.IsTwoFingerBarrel(p, size);
            var barrel = Barrel(p, twoFingers);
            var muzzle = twoFingers ? new PointF((p[8].X + p[12].X) / 2, (p[8].Y + p[12].Y) / 2) : p[8];
            // Elevation: how far the barrel points up, the same for guns pointing left or right.
            var elevation = MathF.Atan2(-barrel.Y, MathF.Abs(barrel.X)) * 180 / MathF.PI;
            // Height of the muzzle above the wrist, in hand sizes.
            var rise = (p[0].Y - muzzle.Y) / size;
            _history.Enqueue(new Sample(now, elevation, rise));
            while (_history.Count > 0 && now - _history.Peek().Time > options.Window)
                _history.Dequeue();

            if (_lastShot is { } last && now - last < options.Cooldown)
                return null;
            var turned = elevation - _history.Min(s => s.Elevation);
            var lifted = rise - _history.Min(s => s.Rise);
            if (turned < options.RecoilDegrees && lifted < options.RecoilRise)
                return null;
            _lastShot = now;
            _history.Clear();
            return (muzzle, barrel);
        }

        /// <summary>Unit vector from the finger bases to the tips: the index finger, or it and the middle one.</summary>
        private static PointF Barrel(PointF[] p, bool twoFingers)
        {
            var x = twoFingers ? (p[8].X + p[12].X - p[5].X - p[9].X) / 2 : p[8].X - p[5].X;
            var y = twoFingers ? (p[8].Y + p[12].Y - p[5].Y - p[9].Y) / 2 : p[8].Y - p[5].Y;
            var length = MathF.Sqrt(x * x + y * y);
            return length > 0 ? new PointF(x / length, y / length) : new PointF(1, 0);
        }

        private readonly record struct Sample(TimeSpan Time, float Elevation, float Rise);
    }
}

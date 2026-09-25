using System.Drawing;

namespace HitCam.Vision.Hands;

/// <summary>
/// A thread between the same fingertip of two hands (thumb–thumb, index–index…). <see cref="Finger"/>: 0 the thumb …
/// 4 the little finger. <see cref="Left"/> is the tip on the hand further left in the picture, <see cref="Right"/> on
/// the other; both normalized to the frame.
/// </summary>
public readonly record struct FingerThread(int Finger, PointF Left, PointF Right);

/// <summary>Where the colour fill goes: between two threads, fingers <see cref="First"/> and <see cref="Second"/>.</summary>
public readonly record struct ThreadFill(int First, int Second);

public sealed record FingerThreadOptions
{
    /// <summary>Tips closer than this (in hand sizes: wrist to the middle finger's base) touch.</summary>
    public float TouchDistance { get; init; } = 0.3f;

    /// <summary>
    /// After a touch, the tips must move at least this far apart (in hand sizes) before the next touch counts: holding
    /// them together does not tie and untie over and over.
    /// </summary>
    public float ReleaseDistance { get; init; } = 0.6f;

    /// <summary>Frames in a row the tips must touch for it to count (a passing hand does not tie one).</summary>
    public int TouchFrames { get; init; } = 2;

    /// <summary>Fingers folded this much (straightness below it) do not tie or untie: a fist bump is not a touch.</summary>
    public float TouchStraightness { get; init; } = 0.6f;
}

/// <summary>
/// Threads between the same fingertips of two hands, switched by touching: touching tips tie a thread, which then
/// stretches between them however the hands move (folding the finger or losing a hand for a while does not break
/// it); touching again unties it. Fills go between neighbouring threads (in finger order). Not thread-safe.
/// </summary>
public sealed class FingerThreads(FingerThreadOptions? options = null)
{
    public static readonly int[] Tips = [4, 8, 12, 16, 20];

    private readonly FingerThreadOptions _options = options ?? new FingerThreadOptions();
    private readonly bool[] _tied = new bool[5];
    private readonly int[] _touching = new int[5];
    // The tips were apart since the last touch: the next touch counts.
    private readonly bool[] _apart = [true, true, true, true, true];

    /// <summary>Unties everything (another camera or picture).</summary>
    public void Reset()
    {
        Array.Clear(_tied);
        Array.Clear(_touching);
        Array.Fill(_apart, true);
    }

    /// <summary>Fingers with a thread, 0 the thumb … 4 the little finger.</summary>
    public IEnumerable<int> Tied => Enumerable.Range(0, 5).Where(f => _tied[f]);

    /// <summary>
    /// This frame's threads and fills; none while there are not two hands (the threads come back with them).
    /// <paramref name="width"/> and <paramref name="height"/>: the frame size, to measure in square pixels.
    /// </summary>
    public (IReadOnlyList<FingerThread> Threads, IReadOnlyList<ThreadFill> Fills) Update(IReadOnlyList<TrackedHand> hands, int width, int height)
    {
        var two = hands.Where(h => h.Points.Count >= HandLandmarker.PointCount).ToList();
        if (two.Count != 2)
        {
            Array.Clear(_touching);
            return ([], []);
        }

        var left = Pixels(two[0], width, height);
        var right = Pixels(two[1], width, height);
        // The hand whose wrist is further left is "left": its tip is the thread's left end, its side gets that colour.
        if (left[0].X > right[0].X)
            (left, right) = (right, left);
        var size = (PoseClassifier.HandSize(left) + PoseClassifier.HandSize(right)) / 2;

        var threads = new List<FingerThread>();
        for (var finger = 0; finger < 5; finger++)
        {
            var tip = Tips[finger];
            var distance = size > 0 ? PoseClassifier.Distance(left[tip], right[tip]) / size : float.MaxValue;
            if (distance > _options.ReleaseDistance)
                _apart[finger] = true;
            var touching = distance < _options.TouchDistance && !IsFolded(left, finger) && !IsFolded(right, finger);
            _touching[finger] = touching ? _touching[finger] + 1 : 0;
            if (_apart[finger] && _touching[finger] >= _options.TouchFrames)
            {
                _tied[finger] = !_tied[finger];
                _apart[finger] = false;
            }
            if (_tied[finger])
            {
                threads.Add(new FingerThread(finger,
                    new PointF(left[tip].X / width, left[tip].Y / height),
                    new PointF(right[tip].X / width, right[tip].Y / height)));
            }
        }

        var fills = new List<ThreadFill>();
        for (var i = 1; i < threads.Count; i++)
            fills.Add(new ThreadFill(threads[i - 1].Finger, threads[i].Finger));
        return (threads, fills);
    }

    private bool IsFolded(PointF[] p, int finger)
    {
        var b = finger * 4 + 1;   // base of the finger: 1, 5, 9, 13, 17
        var length = PoseClassifier.Distance(p[b], p[b + 1]) + PoseClassifier.Distance(p[b + 1], p[b + 2]) + PoseClassifier.Distance(p[b + 2], p[b + 3]);
        return length <= 0 || PoseClassifier.Distance(p[b], p[b + 3]) / length < _options.TouchStraightness;
    }

    private static PointF[] Pixels(TrackedHand hand, int width, int height) =>
        [.. hand.Points.Select(p => new PointF(p.X * width, p.Y * height))];
}

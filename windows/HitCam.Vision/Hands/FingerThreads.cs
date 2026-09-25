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

    /// <summary>Frames in a row the tips must touch before a thread appears (a passing hand does not tie one).</summary>
    public int TouchFrames { get; init; } = 2;

    /// <summary>A finger folded this much (straightness below it) breaks its thread.</summary>
    public float BreakStraightness { get; init; } = 0.6f;
}

/// <summary>
/// Ties threads between the same fingertips of two hands when they touch; a thread then stretches between the tips
/// as the hands move apart, and breaks when its finger folds or either hand is lost. Fills go between neighbouring
/// threads (in finger order). Not thread-safe.
/// </summary>
public sealed class FingerThreads(FingerThreadOptions? options = null)
{
    public static readonly int[] Tips = [4, 8, 12, 16, 20];

    private readonly FingerThreadOptions _options = options ?? new FingerThreadOptions();
    private readonly bool[] _tied = new bool[5];
    private readonly int[] _touching = new int[5];
    private (int A, int B)? _pair;

    public void Reset()
    {
        Array.Clear(_tied);
        Array.Clear(_touching);
        _pair = null;
    }

    /// <summary>
    /// This frame's threads and fills. <paramref name="width"/> and <paramref name="height"/>: the frame size, to
    /// measure in square pixels.
    /// </summary>
    public (IReadOnlyList<FingerThread> Threads, IReadOnlyList<ThreadFill> Fills) Update(IReadOnlyList<TrackedHand> hands, int width, int height)
    {
        var two = hands.Where(h => h.Points.Count >= HandLandmarker.PointCount).Take(3).ToList();
        if (two.Count != 2)
        {
            Reset();
            return ([], []);
        }
        // Another pair of hands than before (one was lost and found again): old threads are gone.
        var ids = (Math.Min(two[0].Id, two[1].Id), Math.Max(two[0].Id, two[1].Id));
        if (_pair != ids)
        {
            Reset();
            _pair = ids;
        }

        var left = Pixels(two[0], width, height);
        var right = Pixels(two[1], width, height);
        // The hand whose wrist is further left gets the "left" colour.
        if (left[0].X > right[0].X)
            (left, right) = (right, left);
        var size = (PoseClassifier.HandSize(left) + PoseClassifier.HandSize(right)) / 2;

        var threads = new List<FingerThread>();
        for (var finger = 0; finger < 5; finger++)
        {
            var tip = Tips[finger];
            if (_tied[finger] && (IsFolded(left, finger) || IsFolded(right, finger)))
            {
                _tied[finger] = false;
                _touching[finger] = 0;
            }
            if (!_tied[finger])
            {
                var touching = size > 0 && PoseClassifier.Distance(left[tip], right[tip]) < _options.TouchDistance * size
                               && !IsFolded(left, finger) && !IsFolded(right, finger);
                _touching[finger] = touching ? _touching[finger] + 1 : 0;
                if (_touching[finger] >= _options.TouchFrames)
                    _tied[finger] = true;
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
        return length <= 0 || PoseClassifier.Distance(p[b], p[b + 3]) / length < _options.BreakStraightness;
    }

    private static PointF[] Pixels(TrackedHand hand, int width, int height) =>
        [.. hand.Points.Select(p => new PointF(p.X * width, p.Y * height))];
}

using System.Drawing;
using HitCam.Vision.Hands;

namespace HitCam.Vision.Tests;

public sealed class FingerThreadTests
{
    private const int Width = 1000;
    private const int Height = 600;
    private const float S = 200;   // wrist to finger bases is 0.3 S = 60 px: the hand size

    /// <summary>
    /// A flat hand: wrist at (<paramref name="wristX"/>, 300), fingers straight along <paramref name="direction"/> (+1
    /// right, -1 left), finger f 0.1 S below the previous one, <paramref name="lengths"/>[f] × S long; folded fingers
    /// curl back.
    /// </summary>
    private static TrackedHand Hand(int id, float wristX, int direction, float[]? lengths = null, int[]? folded = null)
    {
        lengths ??= [0.4f, 0.4f, 0.4f, 0.4f, 0.4f];
        var points = new PointF[21];
        points[0] = new PointF(wristX, 300);
        for (var f = 0; f < 5; f++)
        {
            var b = f * 4 + 1;
            var baseX = wristX + direction * 0.3f * S;
            var y = 300 + (f - 2) * 0.1f * S;
            points[b] = new PointF(baseX, y);
            if (folded?.Contains(f) == true)
            {
                points[b + 1] = new PointF(baseX + direction * 0.15f * S, y);
                points[b + 2] = new PointF(baseX + direction * 0.1f * S, y + 0.05f * S);
                points[b + 3] = new PointF(baseX + direction * 0.02f * S, y + 0.08f * S);
                continue;
            }
            for (var j = 1; j <= 3; j++)
                points[b + j] = new PointF(baseX + direction * lengths[f] * S * j / 3, y);
        }
        return new TrackedHand(id, [.. points.Select(p => new PointF(p.X / Width, p.Y / Height))], 0.95f, true);
    }

    /// <summary>Two hands facing each other, fingertips <paramref name="gap"/> px apart (negative: overlapping).</summary>
    private static TrackedHand[] Facing(float gap, float[]? leftLengths = null, float[]? rightLengths = null, int[]? leftFolded = null)
    {
        // Tips of 0.4 S fingers are 0.7 S from the wrist.
        var leftWrist = 500 - gap / 2 - 0.7f * S;
        var rightWrist = 500 + gap / 2 + 0.7f * S;
        return [Hand(1, leftWrist, 1, leftLengths, leftFolded), Hand(2, rightWrist, -1, rightLengths)];
    }

    private static (IReadOnlyList<FingerThread> Threads, IReadOnlyList<ThreadFill> Fills) Run(FingerThreads threads, params TrackedHand[][] frames)
    {
        (IReadOnlyList<FingerThread>, IReadOnlyList<ThreadFill>) last = ([], []);
        foreach (var hands in frames)
            last = threads.Update(hands, Width, Height);
        return last;
    }

    [Fact]
    public void Hands_apart_tie_nothing()
    {
        var (threads, fills) = Run(new FingerThreads(), Facing(200), Facing(200), Facing(200));
        Assert.Empty(threads);
        Assert.Empty(fills);
    }

    [Fact]
    public void Touching_tips_tie_threads_on_the_second_frame_and_they_stretch()
    {
        var detector = new FingerThreads();
        Assert.Empty(detector.Update(Facing(0), Width, Height).Threads);

        var (threads, _) = detector.Update(Facing(0), Width, Height);
        Assert.Equal([0, 1, 2, 3, 4], threads.Select(t => t.Finger));

        // Pulled 300 px apart: the threads stay and follow the tips; left is the hand on the left.
        (threads, _) = detector.Update(Facing(300), Width, Height);
        var index = Assert.Single(threads, t => t.Finger == 1);
        Assert.Equal((500 - 150) / (float)Width, index.Left.X, 1e-4f);
        Assert.Equal((500 + 150) / (float)Width, index.Right.X, 1e-4f);
        Assert.Equal(280 / (float)Height, index.Left.Y, 1e-4f);   // the index finger is 0.1 S above the middle one
    }

    [Fact]
    public void Only_the_fingers_that_touched_are_tied()
    {
        // Index and middle fingers 0.1 S longer on both hands: their tips meet, the others stay 40 px apart.
        float[] longer = [0.4f, 0.5f, 0.5f, 0.4f, 0.4f];
        var (threads, fills) = Run(new FingerThreads(), Facing(40, longer, longer), Facing(40, longer, longer));

        Assert.Equal([1, 2], threads.Select(t => t.Finger));
        Assert.Equal([new ThreadFill(1, 2)], fills);
    }

    [Fact]
    public void Fills_go_between_neighbouring_threads()
    {
        float[] longer = [0.4f, 0.5f, 0.5f, 0.4f, 0.5f];
        var (threads, fills) = Run(new FingerThreads(), Facing(40, longer, longer), Facing(40, longer, longer));

        Assert.Equal([1, 2, 4], threads.Select(t => t.Finger));
        Assert.Equal([new ThreadFill(1, 2), new ThreadFill(2, 4)], fills);
    }

    [Fact]
    public void A_single_frame_of_touching_ties_nothing()
    {
        Assert.Empty(Run(new FingerThreads(), Facing(0), Facing(200), Facing(200)).Threads);
    }

    [Fact]
    public void Folding_a_finger_breaks_its_thread()
    {
        var detector = new FingerThreads();
        Run(detector, Facing(0), Facing(0));

        var (threads, _) = detector.Update(Facing(300, leftFolded: [1]), Width, Height);
        Assert.DoesNotContain(threads, t => t.Finger == 1);
        Assert.Contains(threads, t => t.Finger == 2);

        // Straightened again, far apart: still broken until the tips touch again.
        (threads, _) = detector.Update(Facing(300), Width, Height);
        Assert.DoesNotContain(threads, t => t.Finger == 1);
    }

    [Fact]
    public void Losing_a_hand_breaks_all_threads()
    {
        var detector = new FingerThreads();
        Run(detector, Facing(0), Facing(0));

        Assert.Empty(detector.Update([Facing(300)[0]], Width, Height).Threads);
        Assert.Empty(detector.Update(Facing(300), Width, Height).Threads);
    }

    [Fact]
    public void The_order_of_the_hands_does_not_swap_left_and_right()
    {
        var hands = Facing(0);
        var detector = new FingerThreads();
        detector.Update(hands, Width, Height);
        var (threads, _) = detector.Update([hands[1], hands[0]], Width, Height);
        Assert.All(threads, t => Assert.True(t.Left.X <= t.Right.X));
    }

    [Fact]
    public void A_different_pair_of_hands_starts_over()
    {
        var detector = new FingerThreads();
        Run(detector, Facing(0), Facing(0));
        var other = Facing(300);
        TrackedHand[] newPair = [other[0], other[1] with { Id = 7 }];
        Assert.Empty(detector.Update(newPair, Width, Height).Threads);
    }
}

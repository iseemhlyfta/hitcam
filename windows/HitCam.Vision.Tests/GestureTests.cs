using System.Drawing;
using HitCam.Vision.Hands;

namespace HitCam.Vision.Tests;

/// <summary>Synthetic hand poses: 21 points, upright (fingers up, y down), about 0.47 wide from wrist to middle base.</summary>
internal static class Poses
{
    public static readonly (float X, float Y)[] Gun =
    [
        (0, 0.45f),
        (-0.15f, 0.32f), (-0.28f, 0.2f), (-0.4f, 0.1f), (-0.5f, 0),              // thumb out
        (-0.08f, 0), (-0.08f, -0.17f), (-0.08f, -0.3f), (-0.08f, -0.42f),        // index straight
        (0.03f, -0.02f), (0.03f, -0.2f), (0.03f, -0.33f), (0.03f, -0.46f),       // middle straight, next to it
        (0.13f, 0), (0.15f, -0.08f), (0.13f, 0.02f), (0.11f, 0.08f),             // ring curled
        (0.22f, 0.06f), (0.24f, -0.01f), (0.22f, 0.06f), (0.2f, 0.1f),           // little finger curled
    ];

    /// <summary>Index and middle fingers spread apart ("V").</summary>
    public static readonly (float X, float Y)[] Peace = With(Gun, (10, (0.1f, -0.19f)), (11, (0.17f, -0.32f)), (12, (0.24f, -0.44f)));

    /// <summary>The classic one-finger gun: only the index finger out, the thumb up.</summary>
    public static readonly (float X, float Y)[] OneFingerGun = With(Gun, (10, (0.06f, -0.1f)), (11, (0.04f, 0)), (12, (0.02f, 0.06f)));

    /// <summary>A gun with the thumb folded over the curled fingers.</summary>
    public static readonly (float X, float Y)[] ThumbIn = With(Gun, (2, (-0.12f, 0.15f)), (3, (-0.02f, 0.08f)), (4, (0.06f, 0.05f)));

    /// <summary>Pointing: only the index finger out, the thumb folded.</summary>
    public static readonly (float X, float Y)[] Pointing = With(OneFingerGun, (2, (-0.12f, 0.15f)), (3, (-0.02f, 0.08f)), (4, (0.06f, 0.05f)));

    public static readonly (float X, float Y)[] Fist = With(Pointing, (6, (-0.06f, -0.1f)), (7, (-0.07f, 0)), (8, (-0.07f, 0.06f)));

    public static PointF[] Place((float X, float Y)[] pose, PointF origin, float size, float rotation)
    {
        var (sin, cos) = MathF.SinCos(rotation);
        return [.. pose.Select(p => new PointF(
            origin.X + (p.X * cos - p.Y * sin) * size,
            origin.Y + (p.X * sin + p.Y * cos) * size))];
    }

    private static (float X, float Y)[] With((float X, float Y)[] pose, params (int Index, (float X, float Y) Point)[] changes)
    {
        var copy = pose.ToArray();
        foreach (var (index, point) in changes)
            copy[index] = point;
        return copy;
    }
}

public sealed class PoseClassifierTests
{
    public static TheoryData<float> Rotations => [0, 0.8f, MathF.PI / 2, -MathF.PI / 2, 2.5f, MathF.PI];

    [Theory]
    [MemberData(nameof(Rotations))]
    public void A_finger_gun_is_a_gun_at_any_angle(float rotation)
    {
        Assert.Equal(HandPose.Gun, PoseClassifier.Classify(Poses.Place(Poses.Gun, new PointF(400, 300), 200, rotation)));
        // The other hand: mirrored.
        var mirrored = Poses.Gun.Select(p => (-p.X, p.Y)).ToArray();
        Assert.Equal(HandPose.Gun, PoseClassifier.Classify(Poses.Place(mirrored, new PointF(400, 300), 200, rotation)));
        Assert.Equal(HandPose.Gun, PoseClassifier.Classify(Poses.Place(Poses.OneFingerGun, new PointF(400, 300), 200, rotation)));
    }

    [Theory]
    [MemberData(nameof(Rotations))]
    public void Other_poses_are_not_a_gun(float rotation)
    {
        foreach (var pose in new[] { Poses.Peace, Poses.Pointing, Poses.ThumbIn, Poses.Fist })
            Assert.Equal(HandPose.None, PoseClassifier.Classify(Poses.Place(pose, new PointF(400, 300), 200, rotation)));
        // An open hand.
        Assert.Equal(HandPose.None, PoseClassifier.Classify(SyntheticHand.Points(new PointF(400, 300), 200, rotation)));
    }

    [Fact]
    public void A_tiny_or_incomplete_hand_is_nothing()
    {
        Assert.Equal(HandPose.None, PoseClassifier.Classify(Poses.Place(Poses.Gun, new PointF(400, 300), 5, 0)));
        Assert.Equal(HandPose.None, PoseClassifier.Classify(new PointF[5]));
    }
}

public sealed class GestureDetectorTests
{
    private const int Width = 1280;
    private const int Height = 720;
    private static readonly PointF Origin = new(500, 400);

    // Pointing right: turned 90° clockwise. Turning back (smaller angle) points the barrel up.
    private const float Right = MathF.PI / 2;

    private static TrackedHand Hand(float rotation, PointF? origin = null, (float X, float Y)[]? pose = null) => new(
        1, [.. Poses.Place(pose ?? Poses.Gun, origin ?? Origin, 250, rotation).Select(p => new PointF(p.X / Width, p.Y / Height))], 0.95f, true);

    private static TimeSpan Frame(int i) => TimeSpan.FromSeconds(i / 30.0);

    private static (List<HandPose> Poses, List<(int Frame, Shot Shot)> Shots) Run(GestureDetector detector, IEnumerable<TrackedHand> frames)
    {
        var poses = new List<HandPose>();
        var shots = new List<(int, Shot)>();
        var i = 0;
        foreach (var hand in frames)
        {
            var (hands, fired) = detector.Update([hand], Width, Height, Frame(i));
            poses.Add(hands[0].Pose);
            shots.AddRange(fired.Select(s => (i, s)));
            i++;
        }
        return (poses, shots);
    }

    /// <summary>Held still, then turned up by <paramref name="degrees"/> over <paramref name="frames"/> frames, then held.</summary>
    private static IEnumerable<TrackedHand> Flick(float degrees, int frames, int before = 10, int after = 10)
    {
        for (var i = 0; i < before; i++)
            yield return Hand(Right);
        for (var i = 1; i <= frames; i++)
            yield return Hand(Right - degrees * MathF.PI / 180 * i / frames);
        for (var i = 0; i < after; i++)
            yield return Hand(Right - degrees * MathF.PI / 180);
    }

    [Fact]
    public void The_gun_pose_is_confirmed_on_the_second_frame()
    {
        var (poses, shots) = Run(new GestureDetector(), Enumerable.Repeat(Hand(Right), 4));
        Assert.Equal([HandPose.None, HandPose.Gun, HandPose.Gun, HandPose.Gun], poses);
        Assert.Empty(shots);
    }

    [Fact]
    public void A_sharp_upward_jerk_fires_one_shot_at_the_muzzle()
    {
        var (_, shots) = Run(new GestureDetector(), Flick(35, 3));

        var (frame, shot) = Assert.Single(shots);
        Assert.InRange(frame, 10, 13);
        Assert.Equal(1, shot.HandId);
        // The muzzle is at the fingertips, right of the wrist; the barrel points right and somewhat up.
        Assert.True(shot.Muzzle.X * Width > Origin.X + 50, $"muzzle {shot.Muzzle}");
        Assert.True(shot.Direction.X > 0.5f && shot.Direction.Y < 0, $"direction {shot.Direction}");
        Assert.Equal(1, MathF.Sqrt(shot.Direction.X * shot.Direction.X + shot.Direction.Y * shot.Direction.Y), 1e-3f);
        Assert.InRange(shot.Size * Height, 100, 130); // 250 px × 0.47
    }

    [Fact]
    public void A_one_finger_gun_fires_from_the_index_fingertip()
    {
        var frames = Enumerable.Range(0, 10).Select(_ => Hand(Right, pose: Poses.OneFingerGun))
            .Concat(Enumerable.Range(1, 3).Select(i => Hand(Right - 0.6f * i / 3, pose: Poses.OneFingerGun)));
        var (_, shots) = Run(new GestureDetector(), frames);
        var shot = Assert.Single(shots).Shot;
        var tip = Poses.Place(Poses.OneFingerGun, Origin, 250, Right - 0.6f)[8];
        // Fired mid-jerk, a little before the last frame: within about 20 px of that fingertip.
        Assert.Equal(tip.X / Width, shot.Muzzle.X, 0.03f);
        Assert.Equal(tip.Y / Height, shot.Muzzle.Y, 0.03f);
    }

    [Fact]
    public void A_gun_pointing_left_fires_too()
    {
        var frames = Enumerable.Repeat(Hand(-Right), 10)
            .Concat(Enumerable.Range(1, 3).Select(i => Hand(-Right + 0.6f * i / 3)))
            .Concat(Enumerable.Repeat(Hand(-Right + 0.6f), 5));
        var (_, shots) = Run(new GestureDetector(), frames);
        Assert.True(Assert.Single(shots).Shot.Direction.X < 0);
    }

    [Fact]
    public void Slowly_raising_the_gun_is_not_a_shot()
    {
        // 40° over 1.5 s: aiming, not recoil.
        Assert.Empty(Run(new GestureDetector(), Flick(40, 45)).Shots);
    }

    [Fact]
    public void Moving_the_whole_hand_fast_is_not_a_shot()
    {
        var frames = Enumerable.Range(0, 10).Select(_ => Hand(Right))
            .Concat(Enumerable.Range(1, 6).Select(i => Hand(Right, new PointF(Origin.X + 40 * i, Origin.Y - 30 * i))));
        Assert.Empty(Run(new GestureDetector(), frames).Shots);
    }

    [Fact]
    public void Jerking_an_open_hand_is_not_a_shot()
    {
        var frames = Enumerable.Range(0, 10).Select(_ => Hand(Right, pose: Poses.Peace))
            .Concat(Enumerable.Range(1, 3).Select(i => Hand(Right - 0.6f * i / 3, pose: Poses.Peace)));
        var (poses, shots) = Run(new GestureDetector(), frames);
        Assert.All(poses, p => Assert.Equal(HandPose.None, p));
        Assert.Empty(shots);
    }

    [Fact]
    public void A_second_jerk_right_after_the_first_is_ignored_but_a_later_one_fires()
    {
        var detector = new GestureDetector();
        IEnumerable<TrackedHand> TwoFlicks(int gap)
        {
            foreach (var h in Flick(35, 3, after: 0))
                yield return h;
            for (var i = 0; i < gap; i++)
                yield return Hand(Right);   // back down to aim
            for (var i = 1; i <= 3; i++)
                yield return Hand(Right - 35 * MathF.PI / 180 * i / 3);
            for (var i = 0; i < 5; i++)
                yield return Hand(Right - 35 * MathF.PI / 180);
        }

        Assert.Single(Run(detector, TwoFlicks(3)).Shots);   // 200 ms later: within the cooldown
        detector.Reset();
        Assert.Equal(2, Run(detector, TwoFlicks(15)).Shots.Count);
    }

    [Fact]
    public void The_pose_is_held_briefly_while_the_fingers_blur()
    {
        var frames = new[] { Hand(Right), Hand(Right), Hand(Right, pose: Poses.Peace), Hand(Right, pose: Poses.Peace) }
            .Concat(Enumerable.Repeat(Hand(Right, pose: Poses.Peace), 12));
        var (poses, _) = Run(new GestureDetector(), frames);
        Assert.Equal(HandPose.Gun, poses[3]);      // 67 ms after it was last seen
        Assert.Equal(HandPose.None, poses[^1]);    // 0.5 s after
    }
}

using System.Collections.Concurrent;
using System.Drawing;
using HitCam.Vision.Hands;
using SkiaSharp;

namespace HitCam.Vision.Tests;

/// <summary>
/// Hands "in the frame" for the tracker: the palm detector finds each one's palm, and the landmark model returns a
/// hand's points when the crop covers the hand's center.
/// </summary>
internal sealed class FakeHandModels : IHandModels
{
    public List<PointF[]> Hands { get; } = [];

    /// <summary>Landmark score per hand (by index in <see cref="Hands"/>); 0.95 if not set.</summary>
    public Dictionary<int, float> Scores { get; } = [];

    public int PalmCalls { get; private set; }

    public List<HandRoi> LandmarkCalls { get; } = [];

    public bool IsDisposed { get; private set; }

    public string Provider => "Fake";

    public IReadOnlyList<Palm> DetectPalms(VisionFrame frame, float threshold)
    {
        PalmCalls++;
        return [.. Hands.Select(h =>
        {
            int[] palmPoints = [0, 5, 9, 13, 17];
            var xs = palmPoints.Select(i => h[i].X).ToArray();
            var ys = palmPoints.Select(i => h[i].Y).ToArray();
            var box = RectangleF.FromLTRB(xs.Min(), ys.Min(), xs.Max(), ys.Max());
            return new Palm(box, [h[0], h[5], h[9], h[13], h[17], h[1], h[2]], 0.9f);
        })];
    }

    public HandLandmarks Landmarks(VisionFrame frame, HandRoi roi)
    {
        LandmarkCalls.Add(roi);
        for (var i = 0; i < Hands.Count; i++)
        {
            var center = new PointF(Hands[i].Average(p => p.X), Hands[i].Average(p => p.Y));
            if (roi.Bounds.Contains(center))
                return new HandLandmarks(Hands[i], Scores.GetValueOrDefault(i, 0.95f), 0.9f);
        }
        return new HandLandmarks(new PointF[21], 0.01f, 0.5f);
    }

    public void Dispose() => IsDisposed = true;
}

public sealed class HandTrackerTests
{
    private static readonly VisionFrame Frame = CreateFrame(1000, 500);

    private static VisionFrame CreateFrame(int width, int height)
    {
        var frame = new VisionFrame();
        frame.SetSize(width, height);
        return frame;
    }

    private static TimeSpan At(int frame) => TimeSpan.FromSeconds(frame / 30.0);

    [Fact]
    public void A_new_hand_is_found_and_then_followed_without_the_palm_detector()
    {
        var models = new FakeHandModels();
        models.Hands.Add(SyntheticHand.Points(new PointF(300, 250), 150, 0.3f));
        var tracker = new HandTracker(models, new HandTrackerOptions { MaxHands = 1 });

        var first = Assert.Single(tracker.Update(Frame, At(0)));
        Assert.Equal(1, first.Id);
        Assert.Equal(21, first.Points.Count);
        // Normalized to the frame.
        Assert.Equal(models.Hands[0][0].X / 1000, first.Points[0].X, 1e-4f);
        Assert.Equal(models.Hands[0][0].Y / 500, first.Points[0].Y, 1e-4f);
        Assert.Equal(1, models.PalmCalls);

        // The hand moves a little: followed from its landmarks, same id, no palm detection (it is the only one allowed).
        models.Hands[0] = SyntheticHand.Points(new PointF(320, 255), 150, 0.35f);
        var second = Assert.Single(tracker.Update(Frame, At(1)));
        Assert.Equal(1, second.Id);
        Assert.Equal(1, models.PalmCalls);
        var expectedRoi = HandGeometry.FromLandmarks(SyntheticHand.Points(new PointF(300, 250), 150, 0.3f));
        Assert.Equal(expectedRoi, models.LandmarkCalls[^1]);
    }

    [Fact]
    public void Up_to_two_hands_get_their_own_ids()
    {
        var models = new FakeHandModels();
        models.Hands.Add(SyntheticHand.Points(new PointF(250, 250), 150, 0));
        models.Hands.Add(SyntheticHand.Points(new PointF(750, 250), 150, 0));
        models.Hands.Add(SyntheticHand.Points(new PointF(500, 100), 80, 0));
        var tracker = new HandTracker(models);

        var hands = tracker.Update(Frame, At(0));
        Assert.Equal([1, 2], hands.Select(h => h.Id));

        // Both keep their ids; with two tracked the palm detector is no longer run.
        var calls = models.PalmCalls;
        hands = tracker.Update(Frame, At(1));
        Assert.Equal([1, 2], hands.Select(h => h.Id));
        Assert.Equal(calls, models.PalmCalls);
    }

    [Fact]
    public void A_palm_of_an_already_tracked_hand_is_not_a_new_hand()
    {
        var models = new FakeHandModels();
        models.Hands.Add(SyntheticHand.Points(new PointF(300, 250), 150, 0));
        var tracker = new HandTracker(models);
        tracker.Update(Frame, At(0));

        // Two allowed, one tracked: the palm detector runs and sees the same hand again.
        var hands = tracker.Update(Frame, At(1));
        Assert.Equal(2, models.PalmCalls);
        Assert.Equal(1, Assert.Single(hands).Id);
    }

    [Fact]
    public void A_hand_is_lost_below_the_keep_score_and_comes_back_with_a_new_id()
    {
        var models = new FakeHandModels();
        models.Hands.Add(SyntheticHand.Points(new PointF(300, 250), 150, 0));
        var tracker = new HandTracker(models);
        Assert.Single(tracker.Update(Frame, At(0)));

        // A weaker score than a new hand needs still keeps a tracked one.
        models.Scores[0] = 0.6f;
        Assert.Single(tracker.Update(Frame, At(1)));

        models.Scores[0] = 0.2f;
        Assert.Empty(tracker.Update(Frame, At(2)));

        models.Scores[0] = 0.95f;
        Assert.Equal(2, Assert.Single(tracker.Update(Frame, At(3))).Id);
    }

    [Fact]
    public void A_weak_new_hand_is_not_shown()
    {
        var models = new FakeHandModels();
        models.Hands.Add(SyntheticHand.Points(new PointF(300, 250), 150, 0));
        models.Scores[0] = 0.6f;
        Assert.Empty(new HandTracker(models).Update(Frame, At(0)));
    }

    [Fact]
    public void Two_tracks_that_end_up_on_one_hand_become_one()
    {
        var models = new FakeHandModels();
        models.Hands.Add(SyntheticHand.Points(new PointF(250, 250), 150, 0));
        models.Hands.Add(SyntheticHand.Points(new PointF(750, 250), 150, 0));
        var tracker = new HandTracker(models);
        Assert.Equal(2, tracker.Update(Frame, At(0)).Count);

        // The second hand leaves and the first moves between both crops: both tracks now find the first hand.
        models.Hands.RemoveAt(1);
        models.Hands[0] = SyntheticHand.Points(new PointF(500, 250), 150, 0);
        var hands = tracker.Update(Frame, At(1));
        Assert.True(hands.Count <= 1 || Geometry.Iou(BoundsOf(hands[0]), BoundsOf(hands[1])) < 0.5f);
    }

    [Fact]
    public void Reset_forgets_the_hands()
    {
        var models = new FakeHandModels();
        models.Hands.Add(SyntheticHand.Points(new PointF(300, 250), 150, 0));
        var tracker = new HandTracker(models);
        tracker.Update(Frame, At(0));
        tracker.Reset();
        Assert.Equal(2, Assert.Single(tracker.Update(Frame, At(1))).Id);
    }

    [Fact]
    public void Still_points_stay_put_and_jitter_is_damped()
    {
        var models = new FakeHandModels();
        var center = new PointF(300, 250);
        models.Hands.Add(SyntheticHand.Points(center, 150, 0));
        var tracker = new HandTracker(models);
        var first = Assert.Single(tracker.Update(Frame, At(0))).Points[8];

        float maxJump = 0;
        var previous = first;
        for (var i = 1; i < 30; i++)
        {
            // ±1.5 px of noise on the whole hand.
            var shift = i % 2 == 0 ? 1.5f : -1.5f;
            models.Hands[0] = SyntheticHand.Points(new PointF(center.X + shift, center.Y), 150, 0);
            var tip = Assert.Single(tracker.Update(Frame, At(i))).Points[8];
            maxJump = Math.Max(maxJump, MathF.Abs(tip.X - previous.X) * 1000);
            previous = tip;
        }
        Assert.True(maxJump < 2f, $"The fingertip jumped {maxJump} px (input 3 px).");
    }

    private static RectangleF BoundsOf(TrackedHand hand) => RectangleF.FromLTRB(
        hand.Points.Min(p => p.X), hand.Points.Min(p => p.Y), hand.Points.Max(p => p.X), hand.Points.Max(p => p.Y));
}

public sealed class HandEngineTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class FakeCamera
    {
        private long _latest;

        public ulong Latest { set => Interlocked.Exchange(ref _latest, (long)value); }

        public bool Grab(ulong previous, VisionFrame target)
        {
            var latest = (ulong)Interlocked.Read(ref _latest);
            if (latest == 0 || latest == previous)
                return false;
            target.SetSize(1000, 500);
            target.Sequence = latest;
            return true;
        }
    }

    [Fact]
    public void Runs_the_tracker_and_reports_hands_and_stats()
    {
        var camera = new FakeCamera();
        var models = new FakeHandModels();
        models.Hands.Add(SyntheticHand.Points(new PointF(300, 250), 150, 0));
        using var engine = new HandEngine(camera.Grab, () => models);
        var results = new BlockingCollection<HandResult>();
        engine.ResultReady += results.Add;

        engine.Start();
        camera.Latest = 1;
        Assert.True(results.TryTake(out var result, Wait));
        Assert.Equal(1UL, result.Sequence);
        Assert.Equal((1000, 500), (result.FrameWidth, result.FrameHeight));
        Assert.Single(result.Hands);
        Assert.Equal("Fake", result.Stats.Provider);
        Assert.Equal(VisionState.Running, engine.Status.State);

        engine.Stop();
        Assert.True(SpinWait.SpinUntil(() => engine.Status.State == VisionState.Stopped, Wait));
        Assert.True(models.IsDisposed);
    }

    [Fact]
    public void A_model_that_does_not_load_is_reported_and_retried()
    {
        var camera = new FakeCamera { Latest = 1 };
        var attempts = 0;
        using var engine = new HandEngine(camera.Grab, () =>
        {
            if (++attempts == 1)
                throw new FileNotFoundException("no hand models");
            return new FakeHandModels();
        });
        var statuses = new BlockingCollection<VisionStatus>();
        engine.StatusChanged += statuses.Add;

        engine.Start();
        Assert.Equal("no hand models", WaitFor(statuses, VisionState.Failed).Error);
        engine.Start();
        Assert.Equal("Fake", WaitFor(statuses, VisionState.Running).Provider);
    }

    private static VisionStatus WaitFor(BlockingCollection<VisionStatus> statuses, VisionState state)
    {
        while (statuses.TryTake(out var status, Wait))
        {
            if (status.State == state)
                return status;
        }
        throw new TimeoutException($"No {state} status.");
    }
}

/// <summary>
/// The real MediaPipe models on a frame with two open hands, against a Python reference (OpenCV's warpAffine with the
/// same MediaPipe crops, ONNX Runtime on the CPU). The models are not in the repository: the test looks for vision/output/hands (see vision/README.md) and is skipped without it.
/// </summary>
public sealed class RealHandModelTests
{
    // Wrist and the five fingertips in pixels: from the palm crop, then from the crop placed by those landmarks.
    private static readonly PointF[][] FromPalm =
    [
        [new(89.4f, 337.3f), new(239.0f, 292.3f), new(228.2f, 208.7f), new(193.9f, 187.6f), new(149.9f, 183.6f), new(106.7f, 195.6f)],
        [new(493.4f, 327.8f), new(342.6f, 273.1f), new(389.8f, 181.8f), new(424.7f, 170.1f), new(454.9f, 168.4f), new(501.3f, 178.8f)],
    ];

    private static readonly PointF[][] Tracked =
    [
        [new(83.0f, 343.9f), new(241.6f, 288.9f), new(228.3f, 209.1f), new(195.3f, 186.7f), new(149.8f, 181.1f), new(108.2f, 194.6f)],
        [new(500.2f, 333.7f), new(342.9f, 274.5f), new(390.2f, 181.4f), new(423.4f, 167.1f), new(452.0f, 167.8f), new(499.5f, 180.5f)],
    ];

    private static readonly int[] Checked = [0, 4, 8, 12, 16, 20];

    [Theory]
    [InlineData(ProviderPreference.Auto)]
    [InlineData(ProviderPreference.Cpu)]
    public void Finds_both_hands_and_puts_the_points_on_the_fingers(ProviderPreference preference)
    {
        var directory = FindUp(Path.Combine("vision", "output", "hands"));
        var files = directory is null ? null : HandModelFiles.Find([directory]);
        var photo = directory is null ? null : Path.Combine(directory, "two_hands.png");
        if (files is null || !File.Exists(photo))
        {
            Assert.Skip("vision/output/hands with the hand models and two_hands.png not found.");
            return;
        }

        using var bitmap = SKBitmap.Decode(photo).Copy(SKColorType.Bgra8888);
        var frame = new VisionFrame();
        frame.SetSize(bitmap.Width, bitmap.Height);
        for (var y = 0; y < bitmap.Height; y++)
            bitmap.GetPixelSpan().Slice(y * bitmap.RowBytes, frame.Stride).CopyTo(frame.Pixels[(y * frame.Stride)..]);

        using var models = HandModels.Load(files.Value.Palm, files.Value.Landmarks, preference);
        if (preference == ProviderPreference.Cpu)
            Assert.Equal(Detector.Cpu, models.Provider);
        var tracker = new HandTracker(models);

        var hands = tracker.Update(frame, TimeSpan.Zero);
        // Bilinear sampling differs a little from OpenCV's (fixed point): within 4 px.
        AssertMatches(hands, frame, FromPalm, 4);

        // The next frame is tracked from the landmarks alone: same hands, same ids. The smoothing keeps the points
        // between the two results (a still hand), so they are within 10 px of the tracked reference.
        var again = tracker.Update(frame, TimeSpan.FromSeconds(1 / 30.0));
        Assert.Equal(hands.Select(h => h.Id).Order(), again.Select(h => h.Id).Order());
        AssertMatches(again, frame, Tracked, 10);
    }

    private static void AssertMatches(IReadOnlyList<TrackedHand> hands, VisionFrame frame, PointF[][] reference, float tolerance)
    {
        Assert.Equal(2, hands.Count);
        foreach (var expected in reference)
        {
            // The hand whose wrist is nearest.
            var hand = hands.MinBy(h => Distance(Pixels(h.Points[0], frame), expected[0]))!;
            Assert.True(hand.Score > 0.8f, $"score {hand.Score}");
            for (var i = 0; i < Checked.Length; i++)
            {
                var actual = Pixels(hand.Points[Checked[i]], frame);
                // Hands here are about 170 px tall.
                Assert.True(Distance(actual, expected[i]) < tolerance, $"point {Checked[i]}: {actual} vs {expected[i]}");
            }
        }
    }

    private static PointF Pixels(PointF normalized, VisionFrame frame) =>
        new(normalized.X * frame.Width, normalized.Y * frame.Height);

    private static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static string? FindUp(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }
}

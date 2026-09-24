using System.Collections.Concurrent;
using System.Drawing;

namespace HitCam.Vision.Tests;

public sealed class VisionEngineTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ModelInfo Model(string id) => new()
    {
        Id = id, Name = id, Role = ModelCatalog.RoleOf(id), ModelPath = id + ".onnx", LabelsPath = id + ".labels.json",
        InputWidth = 32, InputHeight = 32, Mean = [0, 0, 0], Std = [1, 1, 1], BoxesOutput = "dets", LogitsOutput = "labels",
        Classes = new Dictionary<int, string> { [1] = "person" },
    };

    /// <summary>A camera whose newest frame the test sets; frames it never handed out were dropped.</summary>
    private sealed class FakeCamera
    {
        private long _latest;

        public ConcurrentQueue<ulong> Delivered { get; } = new();

        public ulong Latest { set => Interlocked.Exchange(ref _latest, (long)value); }

        public bool Grab(ulong previous, VisionFrame target)
        {
            var latest = (ulong)Interlocked.Read(ref _latest);
            if (latest == 0 || latest == previous)
                return false;
            target.SetSize(4, 4);
            target.Sequence = latest;
            Delivered.Enqueue(latest);
            return true;
        }
    }

    /// <summary>Reports one person per frame; optionally waits for the test before finishing each frame.</summary>
    private sealed class FakeDetector(ModelInfo model, bool gated = false) : IObjectDetector
    {
        public ModelInfo Model => model;

        public string Provider => "Fake";

        public SemaphoreSlim Entered { get; } = new(0);

        public SemaphoreSlim Release { get; } = new(0);

        public ConcurrentQueue<ulong> Analysed { get; } = new();

        public bool IsDisposed { get; private set; }

        public IReadOnlyList<Detection> Detect(VisionFrame frame, DetectionOptions options)
        {
            Analysed.Enqueue(frame.Sequence);
            Entered.Release();
            if (gated && !Release.Wait(Wait))
                throw new TimeoutException();
            return [new Detection(1, "person", 0.9f, new RectangleF(0.1f, 0.1f, 0.3f, 0.3f))];
        }

        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public void Frames_arriving_during_analysis_are_skipped_up_to_the_newest()
    {
        var camera = new FakeCamera();
        FakeDetector? detector = null;
        using var engine = new VisionEngine(camera.Grab, m => detector = new FakeDetector(m, gated: true));
        camera.Latest = 1;
        engine.Start(Model("a"));

        Assert.True(SpinWait.SpinUntil(() => detector is not null, Wait));
        Assert.True(detector!.Entered.Wait(Wait, TestContext.Current.CancellationToken));
        // Frames 2..5 arrive while frame 1 is being analysed.
        for (ulong frame = 2; frame <= 5; frame++)
            camera.Latest = frame;
        detector.Release.Release();

        Assert.True(detector.Entered.Wait(Wait, TestContext.Current.CancellationToken));
        detector.Release.Release(10);
        engine.Stop();

        Assert.Equal([1ul, 5ul], detector.Analysed.Take(2));
        // Nothing but those two was ever copied out of the camera.
        Assert.Equal([1ul, 5ul], camera.Delivered.Take(2));
    }

    [Fact]
    public void The_same_frame_is_never_analysed_twice()
    {
        var camera = new FakeCamera { Latest = 7 };
        FakeDetector? detector = null;
        using var engine = new VisionEngine(camera.Grab, m => detector = new FakeDetector(m));
        engine.Start(Model("a"));

        Assert.True(SpinWait.SpinUntil(() => detector is not null && detector.Analysed.Count == 1, Wait));
        Thread.Sleep(100);

        Assert.Equal([7ul], detector!.Analysed);
    }

    [Fact]
    public void Results_carry_tracks_and_stats()
    {
        var camera = new FakeCamera();
        using var engine = new VisionEngine(camera.Grab, m => new FakeDetector(m));
        var results = new BlockingCollection<VisionResult>();
        engine.ResultReady += results.Add;
        engine.Start(Model("a"));

        // The tracker shows an object from its second hit.
        camera.Latest = 1;
        Assert.True(results.TryTake(out var first, Wait));
        camera.Latest = 2;
        Assert.True(results.TryTake(out var second, Wait));

        Assert.Empty(first.Tracks);
        Assert.Equal("person", Assert.Single(second.Tracks).Name);
        Assert.Equal("Fake", second.Stats.Provider);
        Assert.Equal((2ul, 4, 4), (second.Sequence, second.FrameWidth, second.FrameHeight));
        Assert.Equal("a", second.Model.Id);
    }

    [Fact]
    public void Switching_and_stopping_unload_the_previous_model()
    {
        var camera = new FakeCamera();
        var detectors = new ConcurrentDictionary<string, FakeDetector>();
        using var engine = new VisionEngine(camera.Grab, m => detectors[m.Id] = new FakeDetector(m));
        var statuses = new BlockingCollection<VisionStatus>();
        engine.StatusChanged += statuses.Add;

        engine.Start(Model("a"));
        WaitFor(statuses, VisionState.Running, "a");
        engine.Start(Model("a")); // already running: nothing happens
        engine.Start(Model("b"));
        WaitFor(statuses, VisionState.Running, "b");

        Assert.True(detectors["a"].IsDisposed);
        Assert.False(detectors["b"].IsDisposed);

        engine.Stop();
        WaitFor(statuses, VisionState.Stopped, null);
        Assert.True(detectors["b"].IsDisposed);
        Assert.Equal(2, detectors.Count);
    }

    [Fact]
    public void A_model_that_fails_to_load_reports_why_and_can_be_retried()
    {
        var camera = new FakeCamera();
        var attempts = 0;
        using var engine = new VisionEngine(camera.Grab, m =>
            Interlocked.Increment(ref attempts) == 1 ? throw new FileNotFoundException("no such model") : new FakeDetector(m));
        var statuses = new BlockingCollection<VisionStatus>();
        engine.StatusChanged += statuses.Add;

        engine.Start(Model("a"));
        var failed = WaitFor(statuses, VisionState.Failed, "a");
        Assert.Equal("no such model", failed.Error);

        engine.Start(Model("a"));
        Assert.Equal("Fake", WaitFor(statuses, VisionState.Running, "a").Provider);
    }

    [Fact]
    public void Dispose_unloads_the_model()
    {
        var camera = new FakeCamera { Latest = 1 };
        FakeDetector? detector = null;
        var engine = new VisionEngine(camera.Grab, m => detector = new FakeDetector(m));
        engine.Start(Model("a"));
        Assert.True(SpinWait.SpinUntil(() => detector is not null, Wait));

        engine.Dispose();

        Assert.True(detector!.IsDisposed);
    }

    private static VisionStatus WaitFor(BlockingCollection<VisionStatus> statuses, VisionState state, string? model)
    {
        while (statuses.TryTake(out var status, Wait))
        {
            if (status.State == state && status.Model?.Id == model)
                return status;
        }
        throw new TimeoutException($"No {state} status for {model}.");
    }
}

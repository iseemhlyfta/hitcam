using System.Collections.Concurrent;
using HitCam.Vision.Segmentation;
using SkiaSharp;

namespace HitCam.Vision.Tests;

public sealed class MaskFilterTests
{
    private static float[] Uniform(int size, float value) => Enumerable.Repeat(value, size * size).ToArray();

    [Fact]
    public void The_first_mask_passes_as_is_in_bytes()
    {
        var filter = new MaskFilter(4, 4);
        var raw = Uniform(4, 0);
        raw[5] = 1;
        raw[6] = 0.5f;

        var (mask, _) = filter.Apply(raw);

        Assert.Equal(255, mask[5]);
        Assert.Equal(128, mask[6]);
        Assert.Equal(0, mask[0]);
    }

    [Fact]
    public void Small_flicker_at_an_edge_is_damped()
    {
        var filter = new MaskFilter(1, 1);
        filter.Apply([0.5f]);
        var values = new List<int>();
        for (var i = 0; i < 20; i++)
            values.Add(filter.Apply([i % 2 == 0 ? 0.6f : 0.4f]).Mask[0]);

        // The model swings ±0.1 (±26); the mask much less.
        Assert.True(values.Skip(10).Max() - values.Skip(10).Min() < 14, string.Join(",", values));
    }

    [Fact]
    public void A_big_change_is_followed_at_once_so_nobody_is_cut_off()
    {
        var filter = new MaskFilter(1, 1);
        filter.Apply([0f]);

        Assert.Equal(255, filter.Apply([1f]).Mask[0]);
        Assert.Equal(0, filter.Apply([0f]).Mask[0]);
    }

    [Fact]
    public void The_person_box_covers_the_mask_and_nobody_gives_none()
    {
        var filter = new MaskFilter(10, 10);
        var raw = Uniform(10, 0);
        for (var y = 2; y < 8; y++)
            for (var x = 3; x < 6; x++)
                raw[y * 10 + x] = 0.9f;

        var box = filter.Apply(raw).Person;

        Assert.NotNull(box);
        Assert.Equal(0.3f, box.Value.X, 1e-5f);
        Assert.Equal(0.2f, box.Value.Y, 1e-5f);
        Assert.Equal(0.3f, box.Value.Width, 1e-5f);
        Assert.Equal(0.6f, box.Value.Height, 1e-5f);
        filter.Reset();
        Assert.Null(filter.Apply(Uniform(10, 0.1f)).Person);
    }

    [Fact]
    public void Garbage_from_the_model_counts_as_background()
    {
        var (mask, _) = new MaskFilter(1, 3).Apply([float.NaN, float.PositiveInfinity, -3]);
        Assert.Equal([0, 0, 0], mask);
    }
}

public sealed class SegmentEngineTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class FakeModel(string provider = "Fake", bool fail = false) : ISegmentModel
    {
        public int Runs;

        public volatile bool Fail = fail;

        public bool IsDisposed { get; private set; }

        public string Provider => provider;

        public int Size => 8;

        public void Segment(VisionFrame frame, Span<float> mask)
        {
            if (Fail)
                throw new InvalidOperationException("device removed");
            Interlocked.Increment(ref Runs);
            mask[..64].Fill(0);
            mask[27] = mask[28] = mask[35] = mask[36] = 1;   // a 2×2 person in the middle
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class Camera
    {
        private long _latest;

        public ulong Latest { set => Interlocked.Exchange(ref _latest, (long)value); }

        public bool Grab(ulong previous, VisionFrame target)
        {
            var latest = (ulong)Interlocked.Read(ref _latest);
            if (latest == 0 || latest == previous)
                return false;
            target.SetSize(64, 36);
            target.Sequence = latest;
            return true;
        }
    }

    [Fact]
    public void Masks_come_with_the_frame_they_belong_to_and_the_person_box()
    {
        var camera = new Camera();
        using var engine = new SegmentEngine(camera.Grab, () => new FakeModel());
        var results = new BlockingCollection<SegmentResult>();
        engine.ResultReady += results.Add;

        engine.Start();
        camera.Latest = 7;
        Assert.True(results.TryTake(out var result, Wait));

        Assert.Equal(7UL, result.Sequence);
        Assert.Equal((8, 8), (result.Width, result.Height));
        Assert.Equal(255, result.Mask[27]);
        Assert.Equal(0, result.Mask[0]);
        Assert.Equal(new System.Drawing.RectangleF(3 / 8f, 3 / 8f, 2 / 8f, 2 / 8f), result.Person);
        Assert.Equal("Fake", result.Stats.Provider);
    }

    [Fact]
    public void Masks_are_not_made_more_often_than_the_interval()
    {
        var camera = new Camera();
        var model = new FakeModel();
        using var engine = new SegmentEngine(camera.Grab, () => model);
        var results = new BlockingCollection<SegmentResult>();
        engine.ResultReady += results.Add;
        engine.Start();

        // A new frame every 5 ms for half a second: at 15 masks a second, about 8 of them.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (ulong frame = 1; watch.ElapsedMilliseconds < 500; frame++)
        {
            camera.Latest = frame;
            Thread.Sleep(5);
        }
        Assert.InRange(model.Runs, 4, 10);
    }

    [Theory]
    [InlineData(5, 66)]
    [InlineData(30, 100)]
    [InlineData(60, 133)]
    public void Slow_runs_space_the_masks_out(double milliseconds, int intervalMilliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(intervalMilliseconds), SegmentEngine.IntervalFor(milliseconds));

    [Fact]
    public void A_model_that_fails_while_running_is_replaced_by_the_fallback()
    {
        var camera = new Camera();
        var broken = new FakeModel("DirectML");
        using var engine = new SegmentEngine(camera.Grab, () => broken, fallbackFactory: () => new FakeModel());
        var results = new BlockingCollection<SegmentResult>();
        engine.ResultReady += results.Add;
        engine.Start();
        camera.Latest = 1;
        Assert.True(results.TryTake(out _, Wait));

        broken.Fail = true;
        SegmentResult? recovered = null;
        for (ulong frame = 2; frame < 100 && recovered is null; frame++)
        {
            camera.Latest = frame;
            if (results.TryTake(out var result, TimeSpan.FromMilliseconds(200)) && result.Stats.Provider == "Fake")
                recovered = result;
        }
        Assert.NotNull(recovered);
        Assert.True(broken.IsDisposed);
        Assert.Equal(VisionState.Running, engine.Status.State);
    }
}

/// <summary>
/// The real MediaPipe model on the public group photo used for faces: 8 people fill the middle of the frame, the wall
/// above them is background. Skipped without vision/output/segment and vision/output/faces.
/// </summary>
public sealed class RealSegmentModelTests
{
    private static string? FindUp(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    [Theory]
    [InlineData(ProviderPreference.Auto)]
    [InlineData(ProviderPreference.Cpu)]
    public void People_are_foreground_and_the_wall_above_them_is_not(ProviderPreference preference)
    {
        var modelPath = FindUp(Path.Combine("vision", "output", "segment", SegmentModelFiles.FileName));
        var photoPath = FindUp(Path.Combine("vision", "output", "faces", "NASAGroup21.jpg"));
        if (modelPath is null || photoPath is null)
            Assert.Skip("vision/output/segment or the face test photo is missing");

        using var model = SelfieSegmenter.Load(modelPath, preference);
        using var source = SKBitmap.Decode(photoPath);
        using var bitmap = source.Copy(SKColorType.Bgra8888);
        var frame = new VisionFrame();
        frame.SetSize(bitmap.Width, bitmap.Height);
        for (var y = 0; y < bitmap.Height; y++)
            bitmap.GetPixelSpan().Slice(y * bitmap.RowBytes, frame.Stride).CopyTo(frame.Pixels[(y * frame.Stride)..]);
        var mask = new float[model.Size * model.Size];

        model.Segment(frame, mask);

        float At(float x, float y) => mask[(int)(y * model.Size) * model.Size + (int)(x * model.Size)];
        // Faces of the back row (see RealFaceModelTests) and the chests below them.
        Assert.True(At(163 / 1280f, 190 / 853f) > 0.8f, "face of the left person");
        Assert.True(At(580 / 1280f, 400 / 853f) > 0.8f, "chest in the middle");
        // The wall in the top corners.
        Assert.True(At(0.02f, 0.02f) < 0.2f, "top left wall");
        Assert.True(At(0.98f, 0.03f) < 0.3f, "top right wall");
    }
}

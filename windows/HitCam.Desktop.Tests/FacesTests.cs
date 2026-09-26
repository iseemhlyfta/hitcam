using System.Drawing;
using System.Reactive.Concurrency;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HitCam.Desktop.Services;
using HitCam.Desktop.ViewModels;
using HitCam.Desktop.Views;
using HitCam.Vision;
using HitCam.Vision.Faces;
using AvaloniaPoint = Avalonia.Point;
using AvaloniaSize = Avalonia.PixelSize;
using AvaloniaVector = Avalonia.Vector;

namespace HitCam.Desktop.Tests;

public sealed class FacesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HitCam.Tests." + Guid.NewGuid().ToString("N"));
    private readonly HistoricalScheduler _time = new();
    private readonly List<FaceSettings> _applied = [];
    private readonly List<FaceSettings> _saved = [];
    private readonly List<int> _toggled = [];
    private int _forgets;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private FacesViewModel CreateViewModel(FaceSettings? initial = null) =>
        new(initial ?? new FaceSettings(), () => true, _applied.Add, _saved.Add, _toggled.Add, () => _forgets++, _time);

    private static TrackedFace Face(int id, bool hidden, float left = 0.25f, float top = 0.25f, float size = 0.5f) =>
        new(id, new RectangleF(left, top, size, size), hidden, true);

    private static FaceResult Result(params TrackedFace[] faces) => new(faces, new VisionStats(3.6, 30, "DirectML"), 1, 960, 540);

    // Settings

    [Fact]
    public void Settings_default_to_off_with_a_mosaic_hidden_in_the_camera()
    {
        var faces = AppSettings.Parse("""{"serverId":"a"}"""u8)!.Faces;

        Assert.False(faces.Enabled);
        Assert.Equal(FaceEffectKind.Mosaic, faces.Effect);
        Assert.Equal(FaceSettings.DefaultStrength, faces.Strength);
        Assert.True(faces.CameraEffect);
        Assert.False(faces.CameraFrame);
        Assert.Equal(new FaceSettings(), AppSettings.Parse("""{"faces":null}"""u8)!.Faces);
        Assert.Equal(FaceEffectKind.Blur, AppSettings.Parse("""{"faces":{"effect":"blur"}}"""u8)!.Faces.Effect);
    }

    [Fact]
    public void Settings_are_clamped_and_survive_a_save_and_load()
    {
        Assert.Equal(100, new FaceSettings { Strength = 400 }.Strength);
        Assert.Equal(FaceSettings.DefaultFillColor, new FaceSettings { FillColor = "nope" }.FillColor);
        Assert.Equal(FaceEffectKind.Mosaic, new FaceSettings { Effect = (FaceEffectKind)9 }.Effect);

        var path = Path.Combine(_directory, "settings.json");
        var faces = new FaceSettings { Enabled = true, Effect = FaceEffectKind.Fill, Strength = 80, FillColor = "#ff0000", CameraFrame = true };
        (AppSettings.Load(path) with { Faces = faces }).Save(path);

        Assert.Equal(faces, AppSettings.Load(path).Faces);
        Assert.Contains("\"fill\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    // Panel

    [Fact]
    public void Changes_apply_at_once_and_save_after_a_pause()
    {
        var faces = CreateViewModel();

        faces.IsEnabled = true;
        faces.SelectedEffect = faces.Effects[2];

        Assert.Equal(FaceEffectKind.Fill, _applied[^1].Effect);
        Assert.True(faces.IsFill);
        Assert.False(faces.HasStrength);
        Assert.Empty(_saved);
        _time.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Equal(new FaceSettings { Enabled = true, Effect = FaceEffectKind.Fill }, Assert.Single(_saved));
    }

    [Fact]
    public void Clicks_count_only_while_active_and_results_only_show_then()
    {
        var faces = CreateViewModel(new FaceSettings { Enabled = true });

        faces.ToggleCommand.Execute(3).Subscribe();
        faces.ShowResult(Result(Face(3, true)));
        Assert.Empty(_toggled);
        Assert.Empty(faces.Faces);

        faces.IsActive = true;
        faces.ToggleCommand.Execute(3).Subscribe();
        faces.ShowResult(Result(Face(3, true), Face(4, false)));
        Assert.Equal([3], _toggled);
        Assert.Equal(2, faces.Faces.Count);
        Assert.Contains("1", faces.StatsText);

        faces.HideEveryoneCommand.Execute().Subscribe();
        Assert.Equal(1, _forgets);

        faces.IsActive = false;
        Assert.Empty(faces.Faces);
        Assert.False(faces.HasStats);
    }

    // The effect in the preview

    private static byte[] Checker(int width, int height, out int stride)
    {
        stride = width * 4 + 16;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var v = (byte)(((x / 2 + y / 2) % 2) == 1 ? 200 : 20);
                pixels.AsSpan(y * stride + x * 4, 4).Fill(v);
                pixels[y * stride + x * 4 + 3] = 255;
            }
        return pixels;
    }

    [Fact]
    public void Mosaic_makes_even_cells_of_the_average_and_leaves_the_rest()
    {
        var pixels = Checker(640, 360, out var stride);
        var original = (byte[])pixels.Clone();

        FaceEffects.Apply(pixels, 640, 360, stride, new RectangleF(0.25f, 0.25f, 0.25f, 0.5f), FaceEffectKind.Mosaic, 0.5f, 0);

        var cell = FaceEffects.MosaicCell(180, 0.5f);
        Assert.Equal(18, cell);
        var first = pixels[90 * stride + 160 * 4];
        Assert.InRange(first, 107, 113);
        for (var y = 90; y < 90 + cell; y++)
            for (var x = 160; x < 160 + cell; x++)
                Assert.Equal(first, pixels[y * stride + x * 4]);
        Assert.Equal(original[90 * stride + 159 * 4], pixels[90 * stride + 159 * 4]);
        Assert.Equal(original[300 * stride + 200 * 4], pixels[300 * stride + 200 * 4]);
    }

    [Fact]
    public void Blur_smooths_and_fill_paints_the_colour()
    {
        var blurred = Checker(640, 360, out var stride);
        FaceEffects.Apply(blurred, 640, 360, stride, new RectangleF(0.25f, 0.25f, 0.5f, 0.5f), FaceEffectKind.Blur, 1f, 0);
        Assert.InRange(blurred[180 * stride + 320 * 4], 98, 122);
        Assert.InRange(blurred[181 * stride + 321 * 4 + 2], 98, 122);

        var filled = Checker(640, 360, out stride);
        FaceEffects.Apply(filled, 640, 360, stride, new RectangleF(0.25f, 0.25f, 0.5f, 0.5f), FaceEffectKind.Fill, 0.5f, 0x112233);
        Assert.Equal(new byte[] { 0x33, 0x22, 0x11, 0xFF }, filled.AsSpan(180 * stride + 320 * 4, 4).ToArray());
    }

    [Fact]
    public void Boxes_past_the_edges_are_clipped_and_bad_ones_ignored()
    {
        var pixels = Checker(64, 36, out var stride);
        var original = (byte[])pixels.Clone();

        FaceEffects.Apply(pixels, 64, 36, stride, new RectangleF(float.NaN, 0, 1, 1), FaceEffectKind.Fill, 1, 0xFF0000);
        FaceEffects.Apply(pixels, 64, 36, stride, new RectangleF(0.5f, 0.5f, 0, 0), FaceEffectKind.Fill, 1, 0xFF0000);
        FaceEffects.Apply(pixels, 64, 36, 64 * 4 - 4, new RectangleF(0, 0, 1, 1), FaceEffectKind.Fill, 1, 0xFF0000);
        Assert.Equal(original, pixels);

        FaceEffects.Apply(pixels, 64, 36, stride, new RectangleF(0.9f, 0.9f, 1, 1), FaceEffectKind.Blur, 1, 0);
        FaceEffects.Apply(pixels, 64, 36, stride, new RectangleF(-0.5f, -0.5f, 0.6f, 0.6f), FaceEffectKind.Mosaic, 1, 0);
    }

    // The camera

    [Fact]
    public void The_region_layout_matches_the_dll()
    {
        Assert.Equal(36, Marshal.SizeOf<HitCamFaceRegion>());
        Assert.Equal(16, (int)Marshal.OffsetOf<HitCamFaceRegion>(nameof(HitCamFaceRegion.Effect)));
        Assert.Equal(24, (int)Marshal.OffsetOf<HitCamFaceRegion>(nameof(HitCamFaceRegion.Rgb)));
        Assert.Equal(32, (int)Marshal.OffsetOf<HitCamFaceRegion>(nameof(HitCamFaceRegion.FrameRgb)));
    }

    [Fact]
    public void The_camera_gets_hidden_faces_and_squares_as_asked()
    {
        TrackedFace[] faces = [Face(1, true), Face(2, false, 0.6f, 0.1f, 0.2f)];

        var hidden = Assert.Single(FaceStyle.CameraRegions(faces, new FaceSettings { Effect = FaceEffectKind.Blur, Strength = 40 }));
        Assert.Equal((1, 0.4f, 0, 0.25f, 0.75f), (hidden.Effect, hidden.Strength, hidden.Frame, hidden.Left, hidden.Right));

        Assert.Empty(FaceStyle.CameraRegions(faces, new FaceSettings { CameraEffect = false }));

        var framed = FaceStyle.CameraRegions(faces, new FaceSettings { CameraEffect = false, CameraFrame = true });
        Assert.Equal([HitCamFaceRegion.NoEffect, HitCamFaceRegion.NoEffect], framed.Select(r => r.Effect));
        Assert.Equal([FaceStyle.Hidden, FaceStyle.Uncovered], framed.Select(r => r.FrameRgb));

        var fill = FaceStyle.CameraRegions(faces, new FaceSettings { Effect = FaceEffectKind.Fill, FillColor = "#ABCDEF", CameraFrame = true });
        Assert.Equal((2, 0xABCDEFu, 1), (fill[0].Effect, fill[0].Rgb, fill[0].Frame));
        Assert.Equal(HitCamFaceRegion.NoEffect, fill[1].Effect);
    }

    // The overlay in the preview

    [Fact]
    public async Task A_click_on_a_square_toggles_that_face_and_elsewhere_goes_through()
    {
        await ShotOverlayTests.Session.Value.Dispatch(() =>
        {
            var toggled = new List<int>();
            var overlay = new FaceOverlay
            {
                Source = new WriteableBitmap(new AvaloniaSize(640, 360), new AvaloniaVector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul),
                Faces = [Face(7, true, 0.1f, 0.1f, 0.3f), Face(8, false, 0.2f, 0.2f, 0.1f)],
                ToggleCommand = ReactiveUI.ReactiveCommand.Create<int>(toggled.Add),
            };
            var window = new Window { Width = 640, Height = 360, Content = overlay };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(7, overlay.FaceAt(new AvaloniaPoint(100, 50))?.Id);
            Assert.Equal(8, overlay.FaceAt(new AvaloniaPoint(160, 90))?.Id); // the smaller one wins where they overlap
            Assert.Null(overlay.FaceAt(new AvaloniaPoint(500, 300)));
            Assert.False(overlay.HitTest(new AvaloniaPoint(500, 300)));
            Assert.True(overlay.HitTest(new AvaloniaPoint(100, 50)));
            Assert.NotNull(window.CaptureRenderedFrame());
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_click_toggles_a_face_but_a_double_click_for_full_screen_does_not()
    {
        await ShotOverlayTests.Session.Value.Dispatch(async () =>
        {
            var toggled = new List<int>();
            var overlay = new FaceOverlay
            {
                Source = new WriteableBitmap(new AvaloniaSize(640, 360), new AvaloniaVector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul),
                Faces = [Face(7, true, 0.1f, 0.1f, 0.3f)],
                ToggleCommand = ReactiveUI.ReactiveCommand.Create<int>(toggled.Add),
                ToggleDelay = TimeSpan.FromMilliseconds(100),
            };
            var window = new Window { Width = 640, Height = 360, Content = overlay };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var square = new AvaloniaPoint(100, 50);

            window.MouseDown(square, Avalonia.Input.MouseButton.Left);
            window.MouseUp(square, Avalonia.Input.MouseButton.Left);
            window.MouseDown(square, Avalonia.Input.MouseButton.Left);
            window.MouseUp(square, Avalonia.Input.MouseButton.Left);
            await Settle();
            Assert.Empty(toggled);

            window.MouseDown(square, Avalonia.Input.MouseButton.Left);
            window.MouseUp(square, Avalonia.Input.MouseButton.Left);
            await Settle();
            Assert.Equal([7], toggled);

            // A double click whose second click landed beside the square: the preview cancels the first.
            window.MouseDown(square, Avalonia.Input.MouseButton.Left);
            window.MouseUp(square, Avalonia.Input.MouseButton.Left);
            overlay.CancelPendingToggle();
            await Settle();
            Assert.Equal([7], toggled);
            window.Close();
            return true;
        }, CancellationToken.None);

        // Past the toggle delay and the double-click time, so the next click is a single one.
        static Task Settle() => Task.Delay(800);
    }

    // Object labels

    [Fact]
    public void Labels_are_on_by_default_and_off_sends_bare_boxes_to_the_camera()
    {
        Assert.True(AppSettings.Parse("""{"serverId":"a"}"""u8)!.Vision.ShowLabels);
        Assert.False(AppSettings.Parse("""{"vision":{"showLabels":false}}"""u8)!.Vision.ShowLabels);
        Assert.NotEqual(new VisionSettings(), new VisionSettings { ShowLabels = false });

        var sent = new List<HitCamOverlayBox[]>();
        using var overlay = new CameraOverlay(boxes => sent.Add(boxes.ToArray()));
        overlay.SetEnabled(true);
        overlay.ShowLabels = false;
        overlay.Show([new Track(3, 1, "person", 0.92f, new RectangleF(0.1f, 0.2f, 0.3f, 0.4f), 0x2563EB)]);

        var box = Assert.Single(Assert.Single(sent));
        Assert.Equal((0, 0, IntPtr.Zero, 0x2563EBu), (box.LabelWidth, box.LabelHeight, box.Label, box.Rgb));
    }
}

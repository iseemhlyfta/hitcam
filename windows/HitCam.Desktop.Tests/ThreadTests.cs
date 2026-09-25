using System.Reactive.Concurrency;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HitCam.Desktop.Services;
using HitCam.Desktop.ViewModels;
using HitCam.Desktop.Views;
using HitCam.Vision;
using HitCam.Vision.Hands;
using PointF = System.Drawing.PointF;

namespace HitCam.Desktop.Tests;

public sealed class ThreadTests
{
    private static readonly FingerThread Index = new(1, new PointF(0.3f, 0.4f), new PointF(0.7f, 0.42f));
    private static readonly FingerThread Middle = new(2, new PointF(0.3f, 0.5f), new PointF(0.7f, 0.52f));

    private static TrackedHand Hand(int id, float x) =>
        new(id, [.. Enumerable.Range(0, 21).Select(i => new PointF(x + i * 0.002f, 0.5f - i * 0.01f))], 0.9f, true);

    private static HandResult Result(params FingerThread[] threads) => new(
        [Hand(1, 0.2f), Hand(2, 0.7f)], new VisionStats(4, 30, "DirectML"), 1, 960, 540, [],
        threads, [.. threads.Zip(threads.Skip(1), (a, b) => new ThreadFill(a.Finger, b.Finger))]);

    // Settings

    [Fact]
    public void Thread_settings_default_to_on_with_camera_threads_fill_and_shots_but_no_points()
    {
        var hands = AppSettings.Parse("""{"hands":{"enabled":true}}"""u8)!.Hands;
        Assert.True(hands.Threads);
        Assert.Equal(HandSettings.DefaultLeftColor, hands.LeftColor);
        Assert.Equal(HandSettings.DefaultRightColor, hands.RightColor);
        Assert.Equal(50, hands.FillOpacity);
        Assert.False(hands.CameraPoints);
        Assert.True(hands.CameraThreads && hands.CameraFill && hands.CameraShots);
    }

    [Theory]
    [InlineData("#22d3ee", "#22D3EE")]
    [InlineData("a1b2c3", "#A1B2C3")]
    [InlineData("#zzzzzz", HandSettings.DefaultLeftColor)]
    [InlineData("", HandSettings.DefaultLeftColor)]
    [InlineData("#12345", HandSettings.DefaultLeftColor)]
    public void Colours_are_normalized(string input, string expected)
    {
        Assert.Equal(expected, new HandSettings { LeftColor = input }.LeftColor);
    }

    [Fact]
    public void Opacity_is_clamped_and_colours_parse()
    {
        Assert.Equal(100, new HandSettings { FillOpacity = 400 }.FillOpacity);
        Assert.Equal(0, new HandSettings { FillOpacity = -3 }.FillOpacity);
        Assert.Equal(0x22D3EEu, HandSettings.ParseColor("#22D3EE"));
    }

    // Camera scene

    [Fact]
    public void Scene_structs_match_the_dll()
    {
        Assert.Equal(16, Marshal.SizeOf<HitCamSceneDot>());
        Assert.Equal(28, Marshal.SizeOf<HitCamSceneLine>());
        Assert.Equal(60, Marshal.SizeOf<HitCamSceneQuad>());
    }

    [Fact]
    public unsafe void Two_threads_make_a_fill_from_the_left_colour_to_the_right_one()
    {
        var settings = new HandSettings { LeftColor = "#112233", RightColor = "#445566", FillOpacity = 40 };
        var scene = HandCameraScene.Build(Result(Index, Middle), settings);

        var quad = Assert.Single(scene.Quads);
        Assert.Equal((0x112233u, 0x445566u, 0.4f), (quad.RgbFrom, quad.RgbTo, quad.Alpha));
        // Corners: left index, right index, right middle, left middle.
        Assert.Equal((0.3f, 0.4f), (quad.X[0], quad.Y[0]));
        Assert.Equal((0.7f, 0.42f), (quad.X[1], quad.Y[1]));
        Assert.Equal((0.7f, 0.52f), (quad.X[2], quad.Y[2]));
        Assert.Equal((0.3f, 0.5f), (quad.X[3], quad.Y[3]));
        // The gradient runs from the middle of the left edge to the middle of the right edge.
        Assert.Equal(0.3f, quad.FromX, 1e-6f);
        Assert.Equal(0.45f, quad.FromY, 1e-6f);
        Assert.Equal(0.7f, quad.ToX, 1e-6f);
        Assert.Equal(0.47f, quad.ToY, 1e-6f);

        // Each thread: a glow and a white core; no points by default.
        Assert.Equal(4, scene.Lines.Length);
        Assert.All(scene.Lines, l => Assert.Equal(0xFFFFFFu, l.Rgb));
        Assert.Empty(scene.Dots);
    }

    [Fact]
    public void Camera_switches_leave_out_their_elements()
    {
        var result = Result(Index, Middle);
        var none = HandCameraScene.Build(result, new HandSettings { CameraThreads = false, CameraFill = false });
        Assert.True(none.IsEmpty);

        var points = HandCameraScene.Build(result, new HandSettings { CameraThreads = false, CameraFill = false, CameraPoints = true });
        Assert.Equal(2 * 21 * 2, points.Dots.Length);   // a ring and a dot per point
        Assert.Empty(points.Lines);

        var skeleton = HandCameraScene.Build(result, new HandSettings { CameraThreads = false, CameraFill = false, CameraPoints = true, ShowSkeleton = true });
        Assert.Equal(2 * HandStyle.Bones.Count, skeleton.Lines.Length);

        var noThreads = HandCameraScene.Build(result, new HandSettings { Threads = false, CameraPoints = false });
        Assert.True(noThreads.IsEmpty);

        var clear = HandCameraScene.Build(result, new HandSettings { FillOpacity = 0 });
        Assert.Empty(clear.Quads);
    }

    [Fact]
    public void Dots_grow_with_the_hand_within_limits()
    {
        Assert.Equal(0.004f, HandStyle.DotRadius(0.01f, 0), 1e-6f);
        Assert.Equal(0.011f, HandStyle.DotRadius(1f, 0), 1e-6f);
        Assert.Equal(0.006f * 1.3f, HandStyle.DotRadius(0.1f, 8), 1e-6f);
    }

    // Panel

    [Fact]
    public void The_panel_edits_colours_opacity_and_camera_switches()
    {
        var applied = new List<HandSettings>();
        var hands = new HandsViewModel(new HandSettings { Enabled = true }, () => true, applied.Add, _ => { }, new HistoricalScheduler());

        hands.LeftColor = Color.FromRgb(0x10, 0x20, 0x30);
        hands.FillOpacity = 72.4;
        hands.CameraPoints = true;
        hands.CameraShots = false;

        Assert.Equal("#102030", applied[^1].LeftColor);
        Assert.Equal(72, applied[^1].FillOpacity);
        Assert.Equal("72%", hands.FillOpacityText);
        Assert.True(applied[^1].CameraPoints);
        Assert.False(applied[^1].CameraShots);
        Assert.Equal(Color.FromRgb(0x10, 0x20, 0x30), hands.LeftColor);
    }

    [Fact]
    public void Threads_reach_the_preview_only_while_switched_on()
    {
        var hands = new HandsViewModel(new HandSettings { Enabled = true }, () => true, _ => { }, _ => { }, new HistoricalScheduler())
        {
            IsActive = true,
        };
        hands.ShowResult(Result(Index, Middle));
        Assert.Equal(2, hands.Threads.Count);
        Assert.Single(hands.Fills);

        hands.ThreadsEnabled = false;
        hands.ShowResult(Result(Index, Middle));
        Assert.Empty(hands.Threads);
        Assert.Empty(hands.Fills);
    }

    // Preview

    [Fact]
    public async Task The_thread_overlay_draws_without_errors()
    {
        await ShotOverlayTests.Session.Value.Dispatch(() =>
        {
            var overlay = new ThreadOverlay
            {
                Source = new WriteableBitmap(new PixelSize(640, 360), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul),
                Threads = [Index, Middle],
                Fills = [new ThreadFill(1, 2)],
                LeftColor = Colors.Cyan,
                RightColor = Colors.HotPink,
                FillOpacity = 50,
            };
            var window = new Window { Width = 640, Height = 360, Content = overlay };
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            window.Close();
        }, CancellationToken.None);
    }
}

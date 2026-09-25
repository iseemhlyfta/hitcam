using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HitCam.Desktop.Views;
using HitCam.Vision.Hands;
using PointF = System.Drawing.PointF;

namespace HitCam.Desktop.Tests;

/// <summary>An empty application for rendering controls headless, with Skia (so Render really runs).</summary>
public sealed class HeadlessApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class ShotOverlayTests
{
    internal static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

    /// <summary>Renders frames until <paramref name="until"/>, as the app would, on the headless UI thread.</summary>
    private static void RunFrames(Func<bool> until, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!until() && watch.Elapsed < timeout)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    [Fact]
    public async Task A_shot_kicks_the_preview_and_settles_without_breaking_rendering()
    {
        await Session.Value.Dispatch(() =>
        {
            var content = new Panel();
            var overlay = new ShotOverlay
            {
                Source = new WriteableBitmap(new PixelSize(640, 360), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul),
                ShakeTarget = content,
            };
            content.Children.Add(overlay);
            var window = new Window { Width = 640, Height = 360, Content = content };
            window.Show();
            RunFrames(() => false, TimeSpan.FromMilliseconds(50));

            // Rendering must not throw (setting a transform during the render pass used to crash the app).
            overlay.Shots = [new FiredShot(new Shot(1, new PointF(0.5f, 0.5f), new PointF(1, 0), 0.2f), Stopwatch.GetTimestamp())];
            var kicked = false;
            RunFrames(() => kicked = content.RenderTransform is not null, TimeSpan.FromSeconds(1));
            Assert.True(kicked, "the preview is kicked while the shot plays");
            Assert.NotNull(window.CaptureRenderedFrame());

            RunFrames(() => content.RenderTransform is null, TimeSpan.FromSeconds(2));
            Assert.Null(content.RenderTransform);
            window.Close();
        }, CancellationToken.None);
    }
}

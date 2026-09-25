using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HitCam.Desktop.Services;
using HitCam.Vision.Hands;

namespace HitCam.Desktop.Views;

/// <summary>
/// Threads between the same fingertips of the two hands, and the colour fills between neighbouring threads, over the
/// preview; the same shapes the DLL draws into the camera picture (<see cref="HandCameraScene"/>).
/// </summary>
public sealed class ThreadOverlay : Control
{
    public static readonly StyledProperty<IReadOnlyList<FingerThread>?> ThreadsProperty =
        AvaloniaProperty.Register<ThreadOverlay, IReadOnlyList<FingerThread>?>(nameof(Threads));

    public static readonly StyledProperty<IReadOnlyList<ThreadFill>?> FillsProperty =
        AvaloniaProperty.Register<ThreadOverlay, IReadOnlyList<ThreadFill>?>(nameof(Fills));

    /// <summary>The picture under the overlay; only its size is used.</summary>
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<ThreadOverlay, IImage?>(nameof(Source));

    public static readonly StyledProperty<Color> LeftColorProperty =
        AvaloniaProperty.Register<ThreadOverlay, Color>(nameof(LeftColor), Colors.Cyan);

    public static readonly StyledProperty<Color> RightColorProperty =
        AvaloniaProperty.Register<ThreadOverlay, Color>(nameof(RightColor), Colors.HotPink);

    /// <summary>Fill opacity in percent.</summary>
    public static readonly StyledProperty<double> FillOpacityProperty =
        AvaloniaProperty.Register<ThreadOverlay, double>(nameof(FillOpacity), 50);

    static ThreadOverlay()
    {
        AffectsRender<ThreadOverlay>(ThreadsProperty, FillsProperty, SourceProperty, LeftColorProperty, RightColorProperty, FillOpacityProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ThreadOverlay>(false);
    }

    public IReadOnlyList<FingerThread>? Threads
    {
        get => GetValue(ThreadsProperty);
        set => SetValue(ThreadsProperty, value);
    }

    public IReadOnlyList<ThreadFill>? Fills
    {
        get => GetValue(FillsProperty);
        set => SetValue(FillsProperty, value);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Color LeftColor
    {
        get => GetValue(LeftColorProperty);
        set => SetValue(LeftColorProperty, value);
    }

    public Color RightColor
    {
        get => GetValue(RightColorProperty);
        set => SetValue(RightColorProperty, value);
    }

    public double FillOpacity
    {
        get => GetValue(FillOpacityProperty);
        set => SetValue(FillOpacityProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Threads is not { Count: > 0 } threads || Source is not { } source)
            return;
        var picture = OverlayGeometry.Fit(Bounds.Size, source.Size);
        if (picture.Width <= 0)
            return;
        Point Map(System.Drawing.PointF p) => new(picture.X + p.X * picture.Width, picture.Y + p.Y * picture.Height);

        using (context.PushClip(picture))
        {
            var byFinger = threads.ToDictionary(t => t.Finger);
            var opacity = Math.Clamp(FillOpacity, 0, 100) / 100;
            if (opacity > 0)
            {
                foreach (var fill in Fills ?? [])
                {
                    if (!byFinger.TryGetValue(fill.First, out var a) || !byFinger.TryGetValue(fill.Second, out var b))
                        continue;
                    var corners = HandStyle.FillCorners(a, b).Select(Map).ToArray();
                    var geometry = new StreamGeometry();
                    using (var g = geometry.Open())
                    {
                        g.BeginFigure(corners[0], true);
                        for (var i = 1; i < 4; i++)
                            g.LineTo(corners[i]);
                        g.EndFigure(true);
                    }
                    // Left colour at the middle of the left edge, right colour at the middle of the right edge.
                    var from = new Point((corners[0].X + corners[3].X) / 2, (corners[0].Y + corners[3].Y) / 2);
                    var to = new Point((corners[1].X + corners[2].X) / 2, (corners[1].Y + corners[2].Y) / 2);
                    var brush = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(from, RelativeUnit.Absolute),
                        EndPoint = new RelativePoint(to, RelativeUnit.Absolute),
                        Opacity = opacity,
                        GradientStops = { new GradientStop(LeftColor, 0), new GradientStop(RightColor, 1) },
                    };
                    context.DrawGeometry(brush, null, geometry);
                }
            }

            var glow = new ImmutablePen(new ImmutableSolidColorBrush(Colors.White, HandStyle.ThreadGlowAlpha),
                Math.Max(2, HandStyle.ThreadGlowWidth * picture.Height), lineCap: PenLineCap.Round);
            var core = new ImmutablePen(Brushes.White.ToImmutable(), Math.Max(1.5, HandStyle.ThreadWidth * picture.Height),
                lineCap: PenLineCap.Round);
            foreach (var t in threads)
                context.DrawLine(glow, Map(t.Left), Map(t.Right));
            foreach (var t in threads)
                context.DrawLine(core, Map(t.Left), Map(t.Right));
        }
    }
}

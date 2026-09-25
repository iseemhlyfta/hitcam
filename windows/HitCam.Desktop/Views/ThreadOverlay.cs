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

    private static uint Rgb(Color color) => (uint)(color.R << 16 | color.G << 8 | color.B);

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
                    // One gradient axis for the whole ribbon: middle of the left edge to middle of the right edge.
                    var from = Map(new System.Drawing.PointF((a.Left.X + b.Left.X) / 2, (a.Left.Y + b.Left.Y) / 2));
                    var to = Map(new System.Drawing.PointF((a.Right.X + b.Right.X) / 2, (a.Right.Y + b.Right.Y) / 2));
                    var aspect = (float)(picture.Width / picture.Height);
                    foreach (var piece in HandStyle.FillPieces(a, b, aspect))
                    {
                        var corners = piece.Corners.Select(Map).ToArray();
                        var geometry = new StreamGeometry();
                        using (var g = geometry.Open())
                        {
                            g.BeginFigure(corners[0], true);
                            for (var i = 1; i < 4; i++)
                                g.LineTo(corners[i]);
                            g.EndFigure(true);
                        }
                        var (rgbFrom, rgbTo) = HandStyle.FillColors(Rgb(LeftColor), Rgb(RightColor), fill.First, piece.Side);
                        var brush = new LinearGradientBrush
                        {
                            StartPoint = new RelativePoint(from, RelativeUnit.Absolute),
                            EndPoint = new RelativePoint(to, RelativeUnit.Absolute),
                            Opacity = opacity,
                            GradientStops =
                            {
                                new GradientStop(Color.FromUInt32(0xFF000000 | rgbFrom), 0),
                                new GradientStop(Color.FromUInt32(0xFF000000 | rgbTo), 1),
                            },
                        };
                        context.DrawGeometry(brush, null, geometry);
                    }
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

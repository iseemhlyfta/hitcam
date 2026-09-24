using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HitCam.Vision.Hands;

namespace HitCam.Desktop.Views;

/// <summary>
/// Draws tracked hands over the preview: a dot on every joint and fingertip, coloured by finger, and optionally the
/// lines between them. Lies over the preview <see cref="Image"/> and maps points through the same uniform scaling.
/// </summary>
public sealed class HandOverlay : Control
{
    public static readonly StyledProperty<IReadOnlyList<TrackedHand>?> HandsProperty =
        AvaloniaProperty.Register<HandOverlay, IReadOnlyList<TrackedHand>?>(nameof(Hands));

    /// <summary>The picture under the overlay; only its size is used.</summary>
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<HandOverlay, IImage?>(nameof(Source));

    public static readonly StyledProperty<bool> ShowSkeletonProperty =
        AvaloniaProperty.Register<HandOverlay, bool>(nameof(ShowSkeleton));

    /// <summary>Pairs of landmarks joined by a line: the palm, then each finger from its base.</summary>
    public static IReadOnlyList<(int From, int To)> Connections { get; } =
    [
        (0, 1), (0, 5), (5, 9), (9, 13), (13, 17), (0, 17),
        (1, 2), (2, 3), (3, 4),
        (5, 6), (6, 7), (7, 8),
        (9, 10), (10, 11), (11, 12),
        (13, 14), (14, 15), (15, 16),
        (17, 18), (18, 19), (19, 20),
    ];

    /// <summary>Finger of a landmark: 0 the wrist, 1 the thumb … 5 the little finger.</summary>
    public static int FingerOf(int landmark) => landmark == 0 ? 0 : (landmark - 1) / 4 + 1;

    public static bool IsFingertip(int landmark) => landmark is 4 or 8 or 12 or 16 or 20;

    // Wrist, thumb, index, middle, ring, little finger: bright on dark and light pictures, with a dark outline.
    private static readonly IBrush[] FingerBrushes =
    [
        new ImmutableSolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
        new ImmutableSolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        new ImmutableSolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
        new ImmutableSolidColorBrush(Color.FromRgb(0x06, 0xB6, 0xD4)),
        new ImmutableSolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        new ImmutableSolidColorBrush(Color.FromRgb(0xD9, 0x46, 0xEF)),
    ];

    private static readonly IPen Outline = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 1.5);
    private static readonly IPen Bone = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), 2,
        lineCap: PenLineCap.Round);

    static HandOverlay()
    {
        AffectsRender<HandOverlay>(HandsProperty, SourceProperty, ShowSkeletonProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<HandOverlay>(false);
    }

    public IReadOnlyList<TrackedHand>? Hands
    {
        get => GetValue(HandsProperty);
        set => SetValue(HandsProperty, value);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool ShowSkeleton
    {
        get => GetValue(ShowSkeletonProperty);
        set => SetValue(ShowSkeletonProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Hands is not { Count: > 0 } hands || Source is not { } source)
            return;
        var picture = OverlayGeometry.Fit(Bounds.Size, source.Size);
        if (picture.Width <= 0)
            return;

        using (context.PushClip(picture))
        {
            foreach (var hand in hands)
            {
                if (hand.Points.Count < HandLandmarker.PointCount)
                    continue;
                var points = hand.Points.Select(p => new Point(picture.X + p.X * picture.Width, picture.Y + p.Y * picture.Height)).ToArray();
                // Dots grow with the hand, within limits: readable on a small preview, not blobs in full screen.
                var size = Math.Max(Distance(points[0], points[9]), 1);
                var radius = Math.Clamp(size * 0.06, 2.5, 6);

                if (ShowSkeleton)
                {
                    foreach (var (from, to) in Connections)
                        context.DrawLine(Bone, points[from], points[to]);
                }
                for (var i = 0; i < points.Length; i++)
                {
                    var r = IsFingertip(i) ? radius * 1.3 : radius;
                    context.DrawEllipse(FingerBrushes[FingerOf(i)], Outline, points[i], r, r);
                }
            }
        }
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

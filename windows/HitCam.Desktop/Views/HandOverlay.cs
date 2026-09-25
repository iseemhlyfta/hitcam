using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HitCam.Desktop.Services;
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
    public static IReadOnlyList<(int From, int To)> Connections => HandStyle.Bones;

    public static int FingerOf(int landmark) => HandStyle.FingerOf(landmark);

    public static bool IsFingertip(int landmark) => HandStyle.IsFingertip(landmark);

    // The same colours and sizes as in the camera picture (HandStyle).
    private static readonly IBrush[] FingerBrushes =
        [.. HandStyle.FingerColors.Select(rgb => new ImmutableSolidColorBrush(Color.FromUInt32(0xFF000000 | rgb)))];

    private static readonly IBrush OutlineBrush = new ImmutableSolidColorBrush(Color.FromUInt32(0xFF000000 | HandStyle.Outline));

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
                // Sizes are fractions of the picture height, as in the camera; never below a readable minimum.
                var size = HandStyle.HandSize(hand.Points, (float)(picture.Width / picture.Height));

                if (ShowSkeleton)
                {
                    var bone = new ImmutablePen(new ImmutableSolidColorBrush(Colors.White, HandStyle.BoneAlpha),
                        Math.Max(1.5, HandStyle.BoneWidth * picture.Height), lineCap: PenLineCap.Round);
                    foreach (var (from, to) in HandStyle.Bones)
                        context.DrawLine(bone, points[from], points[to]);
                }
                for (var i = 0; i < points.Length; i++)
                {
                    var r = Math.Max(2.5, HandStyle.DotRadius(size, i) * picture.Height);
                    var ring = r + Math.Max(1, HandStyle.OutlineWidth * picture.Height);
                    context.DrawEllipse(OutlineBrush, null, points[i], ring, ring);
                    context.DrawEllipse(FingerBrushes[HandStyle.FingerOf(i)], null, points[i], r, r);
                }
            }
        }
    }
}

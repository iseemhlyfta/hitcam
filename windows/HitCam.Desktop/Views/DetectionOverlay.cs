using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HitCam.Vision;

namespace HitCam.Desktop.Views;

/// <summary>Where normalized boxes land on screen over an image shown with <c>Stretch="Uniform"</c>.</summary>
public static class OverlayGeometry
{
    /// <summary>The part of <paramref name="container"/> the picture covers: scaled to fit, centered (letterbox).</summary>
    public static Rect Fit(Size container, Size picture)
    {
        if (picture.Width <= 0 || picture.Height <= 0 || container.Width <= 0 || container.Height <= 0)
            return default;
        var scale = Math.Min(container.Width / picture.Width, container.Height / picture.Height);
        var width = picture.Width * scale;
        var height = picture.Height * scale;
        return new Rect((container.Width - width) / 2, (container.Height - height) / 2, width, height);
    }

    /// <summary>A box normalized to the picture (0..1) in the coordinates of the container.</summary>
    public static Rect Map(System.Drawing.RectangleF box, Rect picture) => new(
        picture.X + box.Left * picture.Width,
        picture.Y + box.Top * picture.Height,
        box.Width * picture.Width,
        box.Height * picture.Height);

    /// <summary>
    /// The label tab: on the box's top-left corner, above the box if there is room in the picture, inside otherwise;
    /// never past the picture's right edge.
    /// </summary>
    public static Rect LabelTab(Rect box, Size tab, Rect picture)
    {
        var x = Math.Max(picture.X, Math.Min(box.X, picture.Right - tab.Width));
        var y = box.Y - tab.Height >= picture.Y ? box.Y - tab.Height : box.Y;
        return new Rect(x, y, tab.Width, tab.Height);
    }
}

/// <summary>
/// Draws tracked objects over the preview: a 2 px outline in the track's colour and a tab with "person 92%" in white.
/// Lies over the preview <see cref="Image"/> and maps boxes through the same uniform scaling.
/// </summary>
public sealed class DetectionOverlay : Control
{
    public static readonly StyledProperty<IReadOnlyList<Track>?> TracksProperty =
        AvaloniaProperty.Register<DetectionOverlay, IReadOnlyList<Track>?>(nameof(Tracks));

    /// <summary>The picture under the overlay; only its size is used.</summary>
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<DetectionOverlay, IImage?>(nameof(Source));

    private static readonly Typeface LabelTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private const double LabelFontSize = 12;
    private const double Outline = 2;

    private readonly Dictionary<uint, (IBrush Brush, IPen Pen)> _brushes = [];

    static DetectionOverlay()
    {
        AffectsRender<DetectionOverlay>(TracksProperty, SourceProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<DetectionOverlay>(false);
    }

    public IReadOnlyList<Track>? Tracks
    {
        get => GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Tracks is not { Count: > 0 } tracks || Source is not { } source)
            return;
        var picture = OverlayGeometry.Fit(Bounds.Size, source.Size);
        if (picture.Width <= 0)
            return;

        using (context.PushClip(picture))
        {
            foreach (var track in tracks)
            {
                var (brush, pen) = BrushesFor(track.Rgb);
                var box = OverlayGeometry.Map(track.Box, picture);
                // The outline is drawn inside the box, so boxes touching the picture's edge keep all four sides.
                context.DrawRectangle(null, pen, box.Deflate(Outline / 2));

                var text = new FormattedText(track.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    LabelTypeface, LabelFontSize, Brushes.White);
                var tab = OverlayGeometry.LabelTab(box, new Size(Math.Ceiling(text.Width) + 10, Math.Ceiling(text.Height) + 4), picture);
                context.DrawRectangle(brush, null, tab, 3, 3);
                context.DrawText(text, new Point(tab.X + 5, tab.Y + 2));
            }
        }
    }

    private (IBrush Brush, IPen Pen) BrushesFor(uint rgb)
    {
        if (!_brushes.TryGetValue(rgb, out var brushes))
        {
            var brush = new ImmutableSolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brushes = (brush, new ImmutablePen(brush, Outline));
            _brushes[rgb] = brushes;
        }
        return brushes;
    }
}

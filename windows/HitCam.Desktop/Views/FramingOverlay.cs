using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace HitCam.Desktop.Views;

/// <summary>
/// Auto-framing over the preview: the part the camera shows in a thin frame, the rest dimmed. The preview itself stays
/// the whole picture, so every other overlay keeps its coordinates. Draws only, takes no pointer input.
/// </summary>
public sealed class FramingOverlay : Control
{
    public static readonly StyledProperty<System.Drawing.RectangleF?> CropProperty =
        AvaloniaProperty.Register<FramingOverlay, System.Drawing.RectangleF?>(nameof(Crop));

    /// <summary>The picture under the overlay; only its size is used.</summary>
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<FramingOverlay, IImage?>(nameof(Source));

    private static readonly IBrush Dim = new ImmutableSolidColorBrush(Color.FromArgb(110, 0, 0, 0));
    private static readonly IPen Outline = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.5);

    static FramingOverlay()
    {
        AffectsRender<FramingOverlay>(CropProperty, SourceProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<FramingOverlay>(false);
    }

    public System.Drawing.RectangleF? Crop
    {
        get => GetValue(CropProperty);
        set => SetValue(CropProperty, value);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Crop is not { } crop || Source is not { } source)
            return;
        var picture = OverlayGeometry.Fit(Bounds.Size, source.Size);
        if (picture.Width <= 0)
            return;
        var box = OverlayGeometry.Map(crop, picture).Intersect(picture);
        // The four bands outside the crop.
        context.FillRectangle(Dim, new Rect(picture.X, picture.Y, picture.Width, box.Y - picture.Y));
        context.FillRectangle(Dim, new Rect(picture.X, box.Bottom, picture.Width, picture.Bottom - box.Bottom));
        context.FillRectangle(Dim, new Rect(picture.X, box.Y, box.X - picture.X, box.Height));
        context.FillRectangle(Dim, new Rect(box.Right, box.Y, picture.Right - box.Right, box.Height));
        context.DrawRectangle(null, Outline, box.Deflate(0.75));
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HitCam.Desktop.Views;

/// <summary>
/// Auto-framing in the preview: the picture and everything drawn on it zoomed to the part the camera shows, the rest cut
/// off, so the preview is what the call sees. Done with a render transform: the children keep their coordinates over
/// the whole picture, and pointer input (a click on a face) goes through the same transform.
/// </summary>
public sealed class FramedPanel : Panel
{
    public static readonly StyledProperty<System.Drawing.RectangleF?> CropProperty =
        AvaloniaProperty.Register<FramedPanel, System.Drawing.RectangleF?>(nameof(Crop));

    /// <summary>The picture shown in the panel (<c>Stretch="Uniform"</c>); only its size is used.</summary>
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<FramedPanel, IImage?>(nameof(Source));

    static FramedPanel()
    {
        CropProperty.Changed.AddClassHandler<FramedPanel>((panel, _) => panel.Frame());
        SourceProperty.Changed.AddClassHandler<FramedPanel>((panel, _) => panel.Frame());
        BoundsProperty.Changed.AddClassHandler<FramedPanel>((panel, _) => panel.Frame());
    }

    public FramedPanel() => RenderTransformOrigin = RelativePoint.TopLeft;

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

    private void Frame()
    {
        var picture = Source is { } source ? OverlayGeometry.Fit(Bounds.Size, source.Size) : default;
        if (Crop is not { Width: > 0, Height: > 0 } crop || picture.Width <= 0)
        {
            RenderTransform = null;
            Clip = null;
            return;
        }
        // The crop keeps the picture's proportions, so one scale maps it onto the picture's place.
        var box = OverlayGeometry.Map(crop, picture);
        var scale = picture.Width / box.Width;
        RenderTransform = new MatrixTransform(
            Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(picture.X - box.X * scale, picture.Y - box.Y * scale));
        Clip = new RectangleGeometry(box);
    }
}

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;
using HitCam.Desktop.Services;
using HitCam.Vision.Faces;

namespace HitCam.Desktop.Views;

/// <summary>
/// Squares around the tracked faces over the preview: hidden ones in violet with a lock, uncovered ones in green. A
/// click on a square runs <see cref="ToggleCommand"/> with the face's id; clicks elsewhere go through to the preview.
/// The effect itself is already in the picture (applied to the preview bitmap).
/// </summary>
public sealed class FaceOverlay : Control, ICustomHitTest
{
    public static readonly StyledProperty<IReadOnlyList<TrackedFace>?> FacesProperty =
        AvaloniaProperty.Register<FaceOverlay, IReadOnlyList<TrackedFace>?>(nameof(Faces));

    /// <summary>The picture under the overlay; only its size is used.</summary>
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<FaceOverlay, IImage?>(nameof(Source));

    public static readonly StyledProperty<ICommand?> ToggleCommandProperty =
        AvaloniaProperty.Register<FaceOverlay, ICommand?>(nameof(ToggleCommand));

    private const double Outline = 2;
    private static readonly ImmutableSolidColorBrush HiddenBrush = Brush(FaceStyle.Hidden);
    private static readonly ImmutableSolidColorBrush UncoveredBrush = Brush(FaceStyle.Uncovered);
    private static readonly IPen HiddenPen = new ImmutablePen(HiddenBrush, Outline);
    private static readonly IPen UncoveredPen = new ImmutablePen(UncoveredBrush, Outline);
    private static readonly IPen ShacklePen = new ImmutablePen(Brushes.White, 1.6);

    static FaceOverlay()
    {
        AffectsRender<FaceOverlay>(FacesProperty, SourceProperty);
        CursorProperty.OverrideDefaultValue<FaceOverlay>(new Cursor(StandardCursorType.Hand));
    }

    public IReadOnlyList<TrackedFace>? Faces
    {
        get => GetValue(FacesProperty);
        set => SetValue(FacesProperty, value);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public ICommand? ToggleCommand
    {
        get => GetValue(ToggleCommandProperty);
        set => SetValue(ToggleCommandProperty, value);
    }

    /// <summary>The face under <paramref name="point"/> (control coordinates); the smallest if squares overlap.</summary>
    public TrackedFace? FaceAt(Point point)
    {
        if (Faces is not { Count: > 0 } faces || Source is not { } source)
            return null;
        var picture = OverlayGeometry.Fit(Bounds.Size, source.Size);
        if (picture.Width <= 0)
            return null;
        TrackedFace? best = null;
        foreach (var face in faces)
        {
            if (OverlayGeometry.Map(face.Box, picture).Intersect(picture).Contains(point)
                && (best is null || face.Box.Width * face.Box.Height < best.Box.Width * best.Box.Height))
                best = face;
        }
        return best;
    }

    // Only the squares take the pointer: the rest of the preview keeps its double-click (full screen).
    public bool HitTest(Point point) => IsVisible && FaceAt(point) is not null;

    /// <summary>
    /// How long a click on a square waits for a second one; null: the system's double-click time. A double click
    /// (full screen) must not uncover a hidden face, in the preview and in the camera alike.
    /// </summary>
    public TimeSpan? ToggleDelay { get; set; }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount > 1)
        {
            // The second click of a double click: the first one does not count either.
            _pendingToggle?.Dispose();
            _pendingToggle = null;
            return;
        }
        if (FaceAt(e.GetPosition(this)) is { } face && ToggleCommand is { } command && command.CanExecute(face.Id))
        {
            var id = face.Id;
            var delay = ToggleDelay ?? Avalonia.VisualTree.VisualExtensions.GetPlatformSettings(this)?.GetDoubleTapTime(e.Pointer.Type) ?? TimeSpan.FromMilliseconds(500);
            _pendingToggle?.Dispose();
            _pendingToggle = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                _pendingToggle = null;
                if (command.CanExecute(id))
                    command.Execute(id);
            }, delay);
            e.Handled = true;
        }
    }

    private IDisposable? _pendingToggle;

    public override void Render(DrawingContext context)
    {
        if (Faces is not { Count: > 0 } faces || Source is not { } source)
            return;
        var picture = OverlayGeometry.Fit(Bounds.Size, source.Size);
        if (picture.Width <= 0)
            return;

        using (context.PushClip(picture))
        {
            foreach (var face in faces)
            {
                var box = OverlayGeometry.Map(face.Box, picture);
                context.DrawRectangle(null, face.Hidden ? HiddenPen : UncoveredPen, box.Deflate(Outline / 2));
                if (face.Hidden)
                    DrawLock(context, new Point(Math.Max(picture.X, box.X), Math.Max(picture.Y, box.Y)));
            }
        }
    }

    /// <summary>A small padlock on a violet tab at the square's top-left corner.</summary>
    private static void DrawLock(DrawingContext context, Point corner)
    {
        var tab = new Rect(corner.X, corner.Y, 18, 18);
        context.DrawRectangle(HiddenBrush, null, tab, 0, 0);
        var body = new Rect(tab.X + 5, tab.Y + 8, 8, 6);
        context.DrawRectangle(Brushes.White, null, body, 1, 1);
        var shackle = new StreamGeometry();
        using (var g = shackle.Open())
        {
            g.BeginFigure(new Point(body.X + 1.5, body.Y), false);
            g.LineTo(new Point(body.X + 1.5, body.Y - 2));
            g.ArcTo(new Point(body.Right - 1.5, body.Y - 2), new Size(2.5, 2.5), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(body.Right - 1.5, body.Y));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, ShacklePen, shackle);
    }

    private static ImmutableSolidColorBrush Brush(uint rgb) => new(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
}

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HitCam.Desktop.Services;
using HitCam.Vision.Hands;

namespace HitCam.Desktop.Views;

/// <summary>A shot and when the app received it (<see cref="Stopwatch.GetTimestamp"/>).</summary>
public sealed record FiredShot(Shot Shot, long Timestamp);

/// <summary>
/// Plays finger-gun shots over the preview, like the DLL does in the camera picture (<see cref="ShotEffect"/>): a
/// flame at the muzzle in a warm glow and a flash of the whole picture, drawn here, and the kick, applied to
/// <see cref="ShakeTarget"/> (the panel holding the preview and its overlays). Animates itself while a shot plays.
/// </summary>
public sealed class ShotOverlay : Control
{
    public static readonly StyledProperty<IReadOnlyList<FiredShot>?> ShotsProperty =
        AvaloniaProperty.Register<ShotOverlay, IReadOnlyList<FiredShot>?>(nameof(Shots));

    /// <summary>The picture under the overlay; only its size is used.</summary>
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<ShotOverlay, IImage?>(nameof(Source));

    public static readonly StyledProperty<Visual?> ShakeTargetProperty =
        AvaloniaProperty.Register<ShotOverlay, Visual?>(nameof(ShakeTarget));

    private static readonly Color Warm = Color.FromRgb(0xFF, 0xB3, 0x47);

    private bool _frameRequested;

    static ShotOverlay()
    {
        AffectsRender<ShotOverlay>(ShotsProperty, SourceProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ShotOverlay>(false);
    }

    public IReadOnlyList<FiredShot>? Shots
    {
        get => GetValue(ShotsProperty);
        set => SetValue(ShotsProperty, value);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Visual? ShakeTarget
    {
        get => GetValue(ShakeTargetProperty);
        set => SetValue(ShakeTargetProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var playing = (Shots ?? []).Select(s => (s.Shot, Ms: Stopwatch.GetElapsedTime(s.Timestamp).TotalMilliseconds))
            .Where(s => s.Ms < ShotEffect.DurationMs).ToList();
        var picture = Source is { } source ? OverlayGeometry.Fit(Bounds.Size, source.Size) : default;
        if (playing.Count == 0 || picture.Width <= 0)
        {
            Shake(ShotState.None, picture);
            return;
        }

        var newest = playing.MinBy(s => s.Ms);
        var move = ShotEffect.At(newest.Ms, newest.Shot.Direction.X);
        Shake(move, picture);
        using (context.PushClip(picture))
        {
            if (move.Lift > 0.004f)
                context.FillRectangle(new ImmutableSolidColorBrush(Colors.White, move.Lift), picture);
            foreach (var (shot, ms) in playing)
                DrawFlash(context, shot, ShotEffect.At(ms, shot.Direction.X).Flash, picture);
        }

        // Next animation frame while anything plays.
        if (!_frameRequested && TopLevel.GetTopLevel(this) is { } top)
        {
            _frameRequested = true;
            top.RequestAnimationFrame(_ =>
            {
                _frameRequested = false;
                InvalidateVisual();
            });
        }
    }

    /// <summary>The same flame and glow as the DLL: sizes from the hand, the flame along the barrel.</summary>
    private static void DrawFlash(DrawingContext context, Shot shot, float flash, Rect picture)
    {
        if (flash <= 0.01f)
            return;
        var size = shot.Size * picture.Height;
        var radius = Math.Max(3, 0.75 * size * (0.8 + 0.2 * flash));
        var muzzle = new Point(
            picture.X + shot.Muzzle.X * picture.Width + shot.Direction.X * 0.25 * size,
            picture.Y + shot.Muzzle.Y * picture.Height + shot.Direction.Y * 0.25 * size);

        var glow = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(0.6 * flash * 255), Warm.R, Warm.G, Warm.B), 0),
                new GradientStop(Color.FromArgb(0, Warm.R, Warm.G, Warm.B), 1),
            },
        };
        context.DrawEllipse(glow, null, muzzle, 2 * radius, 2 * radius);

        var flame = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(flash * 255), 255, 255, 255), 0),
                new GradientStop(Color.FromArgb((byte)(flash * 230), Warm.R, Warm.G, Warm.B), 0.55),
                new GradientStop(Color.FromArgb(0, Warm.R, Warm.G, Warm.B), 1),
            },
        };
        var angle = Math.Atan2(shot.Direction.Y, shot.Direction.X);
        var turn = Matrix.CreateTranslation(-muzzle.X, -muzzle.Y) * Matrix.CreateRotation(angle) * Matrix.CreateTranslation(muzzle.X, muzzle.Y);
        using (context.PushTransform(turn))
            context.DrawEllipse(flame, null, new Point(muzzle.X + 0.4 * radius, muzzle.Y), 1.6 * radius, 0.55 * radius);
    }

    /// <summary>Moves and scales the preview panel around the picture's center (the panel's center).</summary>
    private void Shake(ShotState move, Rect picture)
    {
        if (ShakeTarget is not { } target)
            return;
        if (move == ShotState.None || picture.Height <= 0)
        {
            if (target.RenderTransform is not null)
                target.RenderTransform = null;
            return;
        }
        target.RenderTransform = new MatrixTransform(
            Matrix.CreateScale(move.Zoom, move.Zoom) * Matrix.CreateTranslation(move.ShakeX * picture.Height, move.ShakeY * picture.Height));
    }
}

using System.Runtime.InteropServices;
using HitCam.Vision.Hands;

namespace HitCam.Desktop.Services;

/// <summary>Matches <c>HitCamShot</c> in HitCamVCam.dll (packed to 4 bytes, 20 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HitCamShot
{
    /// <summary>Muzzle, normalized to the frame.</summary>
    public float X;
    public float Y;
    /// <summary>Barrel direction in frame pixels (y down).</summary>
    public float DirX;
    public float DirY;
    /// <summary>Hand size as a fraction of the frame height.</summary>
    public float Size;

    public static HitCamShot From(Shot shot) => new()
    {
        X = shot.Muzzle.X, Y = shot.Muzzle.Y, DirX = shot.Direction.X, DirY = shot.Direction.Y, Size = shot.Size,
    };
}

/// <summary>The effect a moment after a shot; see <see cref="ShotEffect.At"/>.</summary>
/// <param name="Flash">Muzzle flash strength 0..1.</param>
/// <param name="Lift">Whole-frame brightening 0..1 (towards white).</param>
/// <param name="ShakeX">Picture offset as a fraction of the frame height.</param>
/// <param name="Zoom">Picture scale ≥ 1, so the shake never shows the frame's edges.</param>
public readonly record struct ShotState(float Flash, float Lift, float ShakeX, float ShakeY, float Zoom)
{
    public static ShotState None { get; } = new(0, 0, 0, 0, 1);
}

/// <summary>
/// The shot effect's curves: a muzzle flash, a flash of the whole frame and a kick that dies out. The same as
/// <c>ShotEffect::At</c> in HitCamVCam.dll, which plays it in the camera picture: keep them in step.
/// </summary>
public static class ShotEffect
{
    public const double DurationMs = 220;
    private const float Shake = 0.018f;

    public static ShotState At(double ms, float dirX)
    {
        if (ms < 0 || ms >= DurationMs)
            return ShotState.None;
        var t = (float)ms;
        var flash = t < 30 ? 1f : 1f - SmoothStep((t - 30) / 80);
        var lift = t < 150 ? 0.22f * MathF.Exp(-t / 40) : 0f;
        var decay = MathF.Exp(-t / 55);
        var swing = decay * MathF.Cos(2 * MathF.PI * t / 90);
        // The camera kicks up (the picture jumps down) and back, against the barrel.
        return new ShotState(flash, lift, -0.5f * Shake * swing * (dirX >= 0 ? 1 : -1), Shake * swing, 1 + 2.2f * Shake * decay);
    }

    private static float SmoothStep(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3 - 2 * x);
    }
}

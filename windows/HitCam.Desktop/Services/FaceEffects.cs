using System.Drawing;
using System.Runtime.InteropServices;
using HitCam.Vision.Faces;

namespace HitCam.Desktop.Services;

/// <summary>Matches <c>HitCamFaceRegion</c> in HitCamVCam.dll (36 bytes). Coordinates are normalized to the frame.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HitCamFaceRegion
{
    public float Left, Top, Right, Bottom;
    /// <summary>0 mosaic, 1 blur, 2 fill, 3 none (the outline only).</summary>
    public int Effect;
    /// <summary>0..1.</summary>
    public float Strength;
    public uint Rgb;
    /// <summary>Non-zero: an outline in <see cref="FrameRgb"/>.</summary>
    public int Frame;
    public uint FrameRgb;

    public const int NoEffect = 3;
}

/// <summary>How faces look in the preview and in the camera alike; colours 0xRRGGBB.</summary>
public static class FaceStyle
{
    /// <summary>Square around a hidden face.</summary>
    public const uint Hidden = 0xA78BFA;

    /// <summary>Square around an uncovered face.</summary>
    public const uint Uncovered = 0x22C55E;

    /// <summary>
    /// What goes to the camera for <paramref name="faces"/>: hidden faces with the effect if it is on for the camera,
    /// and squares around all faces if those are. Empty when there is nothing to draw.
    /// </summary>
    public static HitCamFaceRegion[] CameraRegions(IReadOnlyList<TrackedFace> faces, FaceSettings settings)
    {
        var regions = new List<HitCamFaceRegion>(faces.Count);
        foreach (var face in faces)
        {
            var effect = face.Hidden && settings.CameraEffect ? (int)settings.Effect : HitCamFaceRegion.NoEffect;
            if (effect == HitCamFaceRegion.NoEffect && !settings.CameraFrame)
                continue;
            regions.Add(new HitCamFaceRegion
            {
                Left = face.Box.Left,
                Top = face.Box.Top,
                Right = face.Box.Right,
                Bottom = face.Box.Bottom,
                Effect = effect,
                Strength = settings.Strength / 100f,
                Rgb = HandSettings.ParseColor(settings.FillColor),
                Frame = settings.CameraFrame ? 1 : 0,
                FrameRgb = face.Hidden ? Hidden : Uncovered,
            });
        }
        return [.. regions];
    }
}

/// <summary>
/// Hides faces in a BGRA picture (the preview), the same way HitCamVCam.dll does in the camera's NV12 frames:
/// mosaic, blur or a solid fill over a normalized box.
/// </summary>
public static class FaceEffects
{
    /// <summary>Mosaic cell size for a face <paramref name="side"/> pixels across: 1/24 of it at strength 0, 1/6 at 1.</summary>
    public static int MosaicCell(int side, float strength)
    {
        var fraction = 1f / 24 + (1f / 6 - 1f / 24) * Math.Clamp(strength, 0f, 1f);
        return Math.Max(2, (int)MathF.Round(side * fraction, MidpointRounding.AwayFromZero) & ~1);
    }

    /// <summary>Blur radius: 1/40 of the face at strength 0, 1/8 at 1.</summary>
    public static int BlurRadius(int side, float strength)
    {
        var fraction = 1f / 40 + (1f / 8 - 1f / 40) * Math.Clamp(strength, 0f, 1f);
        return Math.Max(1, (int)MathF.Round(side * fraction, MidpointRounding.AwayFromZero));
    }

    /// <summary>Applies <paramref name="effect"/> over <paramref name="box"/> (normalized) of a BGRA picture.</summary>
    public static void Apply(Span<byte> bgra, int width, int height, int stride, RectangleF box, FaceEffectKind effect, float strength, uint rgb)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < (long)stride * (height - 1) + width * 4)
            return;
        if (!float.IsFinite(box.Left) || !float.IsFinite(box.Top) || !float.IsFinite(box.Right) || !float.IsFinite(box.Bottom))
            return;
        var x0 = Math.Clamp((int)MathF.Floor(Math.Clamp(box.Left, 0f, 1f) * width), 0, width);
        var y0 = Math.Clamp((int)MathF.Floor(Math.Clamp(box.Top, 0f, 1f) * height), 0, height);
        var x1 = Math.Clamp((int)MathF.Ceiling(Math.Clamp(box.Right, 0f, 1f) * width), 0, width);
        var y1 = Math.Clamp((int)MathF.Ceiling(Math.Clamp(box.Bottom, 0f, 1f) * height), 0, height);
        if (x1 <= x0 || y1 <= y0)
            return;
        var side = Math.Max(x1 - x0, y1 - y0);
        switch (effect)
        {
            case FaceEffectKind.Mosaic:
                Mosaic(bgra, stride, x0, y0, x1, y1, MosaicCell(side, strength));
                break;
            case FaceEffectKind.Blur:
                Blur(bgra, stride, x0, y0, x1, y1, BlurRadius(side, strength));
                break;
            default:
                Fill(bgra, stride, x0, y0, x1, y1, rgb);
                break;
        }
    }

    private static void Mosaic(Span<byte> bgra, int stride, int x0, int y0, int x1, int y1, int cell)
    {
        for (var by = y0; by < y1; by += cell)
        {
            var ey = Math.Min(by + cell, y1);
            for (var bx = x0; bx < x1; bx += cell)
            {
                var ex = Math.Min(bx + cell, x1);
                int b = 0, g = 0, r = 0, count = (ey - by) * (ex - bx);
                for (var y = by; y < ey; y++)
                {
                    var row = bgra.Slice(y * stride + bx * 4, (ex - bx) * 4);
                    for (var i = 0; i < row.Length; i += 4)
                    {
                        b += row[i];
                        g += row[i + 1];
                        r += row[i + 2];
                    }
                }
                var (ab, ag, ar) = ((byte)((b + count / 2) / count), (byte)((g + count / 2) / count), (byte)((r + count / 2) / count));
                for (var y = by; y < ey; y++)
                {
                    var row = bgra.Slice(y * stride + bx * 4, (ex - bx) * 4);
                    for (var i = 0; i < row.Length; i += 4)
                    {
                        row[i] = ab;
                        row[i + 1] = ag;
                        row[i + 2] = ar;
                    }
                }
            }
        }
    }

    /// <summary>Two horizontal and two vertical box passes (close to a Gaussian), per channel, edges repeated.</summary>
    private static void Blur(Span<byte> bgra, int stride, int x0, int y0, int x1, int y1, int radius)
    {
        int cols = x1 - x0, rows = y1 - y0, window = 2 * radius + 1;
        var plane = new int[cols * rows];
        var line = new int[Math.Max(cols, rows)];
        for (var channel = 0; channel < 3; channel++)
        {
            for (var y = 0; y < rows; y++)
                for (var x = 0; x < cols; x++)
                    plane[y * cols + x] = bgra[(y0 + y) * stride + (x0 + x) * 4 + channel];
            for (var pass = 0; pass < 2; pass++)
            {
                for (var y = 0; y < rows; y++)
                    BoxLine(plane, y * cols, 1, cols, radius, window, line);
                for (var x = 0; x < cols; x++)
                    BoxLine(plane, x, cols, rows, radius, window, line);
            }
            for (var y = 0; y < rows; y++)
                for (var x = 0; x < cols; x++)
                    bgra[(y0 + y) * stride + (x0 + x) * 4 + channel] = (byte)plane[y * cols + x];
        }
    }

    private static void BoxLine(int[] plane, int start, int step, int n, int radius, int window, int[] line)
    {
        for (var i = 0; i < n; i++)
            line[i] = plane[start + i * step];
        var sum = 0;
        for (var i = -radius; i <= radius; i++)
            sum += line[Math.Clamp(i, 0, n - 1)];
        for (var i = 0; i < n; i++)
        {
            plane[start + i * step] = (sum + window / 2) / window;
            sum += line[Math.Min(i + radius + 1, n - 1)] - line[Math.Max(i - radius, 0)];
        }
    }

    private static void Fill(Span<byte> bgra, int stride, int x0, int y0, int x1, int y1, uint rgb)
    {
        var pixel = (uint)0xFF000000 | rgb;
        for (var y = y0; y < y1; y++)
            MemoryMarshal.Cast<byte, uint>(bgra.Slice(y * stride + x0 * 4, (x1 - x0) * 4)).Fill(pixel);
    }
}

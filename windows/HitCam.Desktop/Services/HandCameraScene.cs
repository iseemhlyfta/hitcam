using System.Drawing;
using System.Runtime.InteropServices;
using HitCam.Vision.Hands;

namespace HitCam.Desktop.Services;

/// <summary>Matches <c>HitCamSceneDot</c> in HitCamVCam.dll (16 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HitCamSceneDot
{
    public float X, Y, Radius;
    public uint Rgb;
}

/// <summary>Matches <c>HitCamSceneLine</c> in HitCamVCam.dll (28 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HitCamSceneLine
{
    public float X0, Y0, X1, Y1, Width;
    public uint Rgb;
    public float Alpha;
}

/// <summary>Matches <c>HitCamSceneQuad</c> in HitCamVCam.dll (60 bytes): corners in order, a linear gradient.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct HitCamSceneQuad
{
    public fixed float X[4];
    public fixed float Y[4];
    public uint RgbFrom, RgbTo;
    public float FromX, FromY, ToX, ToY;
    public float Alpha;
}

/// <summary>
/// How hands look, in the preview and in the camera alike: sizes are fractions of the picture height (the preview
/// scales them to its size), colours 0xRRGGBB.
/// </summary>
public static class HandStyle
{
    /// <summary>Wrist, thumb, index, middle, ring, little finger.</summary>
    public static readonly uint[] FingerColors = [0xF8FAFC, 0xF59E0B, 0x22C55E, 0x06B6D4, 0x3B82F6, 0xD946EF];

    /// <summary>The dark ring under each dot.</summary>
    public const uint Outline = 0x000000;
    public const float OutlineWidth = 0.0015f;
    public const uint Thread = 0xFFFFFF;

    /// <summary>Thread: a white core in a soft glow.</summary>
    public const float ThreadWidth = 0.0045f;
    public const float ThreadGlowWidth = 0.014f;
    public const float ThreadGlowAlpha = 0.3f;

    public const float BoneWidth = 0.0028f;
    public const float BoneAlpha = 0.8f;

    /// <summary>Finger of a landmark: 0 the wrist, 1 the thumb … 5 the little finger.</summary>
    public static int FingerOf(int landmark) => landmark == 0 ? 0 : (landmark - 1) / 4 + 1;

    public static bool IsFingertip(int landmark) => landmark is 4 or 8 or 12 or 16 or 20;

    /// <summary>
    /// Dot radius for a hand <paramref name="handSize"/> tall (wrist to the middle finger's base), both as fractions of
    /// the picture height: grows with the hand, within limits.
    /// </summary>
    public static float DotRadius(float handSize, int landmark) =>
        Math.Clamp(handSize * 0.06f, 0.004f, 0.011f) * (IsFingertip(landmark) ? 1.3f : 1f);

    /// <summary>Pairs of landmarks joined by a line: the palm, then each finger from its base.</summary>
    public static IReadOnlyList<(int From, int To)> Bones { get; } =
    [
        (0, 1), (0, 5), (5, 9), (9, 13), (13, 17), (0, 17),
        (1, 2), (2, 3), (3, 4),
        (5, 6), (6, 7), (7, 8),
        (9, 10), (10, 11), (11, 12),
        (13, 14), (14, 15), (15, 16),
        (17, 18), (18, 19), (19, 20),
    ];

    /// <summary>A hand's size as a fraction of the picture height.</summary>
    public static float HandSize(IReadOnlyList<PointF> normalized, float aspect)
    {
        var dx = (normalized[0].X - normalized[9].X) * aspect;
        var dy = normalized[0].Y - normalized[9].Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// The four corners of the fill between two threads, in order around it: left tip of the first, right tip of the
    /// first, right tip of the second, left tip of the second.
    /// </summary>
    public static PointF[] FillCorners(FingerThread first, FingerThread second) => [first.Left, first.Right, second.Right, second.Left];

    /// <summary>
    /// The fill between two threads as a ribbon with two sides: one piece, or two when the threads cross (the ribbon
    /// is twisted: the part left of the crossing shows one side, the part right of it the other). Each piece has four
    /// corners (a triangle repeats its crossing point) and <see cref="FillPiece.Side"/> 0 or 1, from the direction its
    /// corners go round: turning the hands over turns the ribbon over. <paramref name="aspect"/>: frame width / height.
    /// </summary>
    public static IReadOnlyList<FillPiece> FillPieces(FingerThread first, FingerThread second, float aspect)
    {
        var a1 = first.Left;
        var b1 = first.Right;
        var b2 = second.Right;
        var a2 = second.Left;
        if (Crossing(a1, b1, a2, b2, aspect) is { } x)
        {
            return
            [
                new FillPiece([a1, x, x, a2], SideOf([a1, x, a2], aspect)),
                new FillPiece([x, b1, b2, x], SideOf([x, b1, b2], aspect)),
            ];
        }
        return [new FillPiece([a1, b1, b2, a2], SideOf([a1, b1, b2, a2], aspect))];
    }

    /// <summary>
    /// Colours of a fill piece, from its left end to its right end: the chosen pair for the first gap's front, the same
    /// pair turned round the colour wheel for every other gap (<paramref name="gap"/>: the first thread's finger,
    /// 0..3) and side, so all eight differ: gap × 45°, the back side 180° further.
    /// </summary>
    public static (uint From, uint To) FillColors(uint left, uint right, int gap, int side)
    {
        var turn = gap * 45f + side * 180f;
        return (RotateHue(left, turn), RotateHue(right, turn));
    }

    /// <summary>The colour with its hue turned by <paramref name="degrees"/> (saturation and value kept).</summary>
    public static uint RotateHue(uint rgb, float degrees)
    {
        var r = ((rgb >> 16) & 0xFF) / 255f;
        var g = ((rgb >> 8) & 0xFF) / 255f;
        var b = (rgb & 0xFF) / 255f;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        float hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        var saturation = max == 0 ? 0 : delta / max;
        hue = ((hue + degrees) % 360 + 360) % 360;
        var c = max * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = max - c;
        var (r1, g1, b1) = (int)(hue / 60) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        static uint Byte(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
        return Byte(r1 + m) << 16 | Byte(g1 + m) << 8 | Byte(b1 + m);
    }

    /// <summary>Where thread a1–b1 crosses thread a2–b2 (inside both), in square pixels; null if they do not.</summary>
    private static PointF? Crossing(PointF a1, PointF b1, PointF a2, PointF b2, float aspect)
    {
        float px = a1.X * aspect, py = a1.Y, rx = (b1.X - a1.X) * aspect, ry = b1.Y - a1.Y;
        float qx = a2.X * aspect, qy = a2.Y, sx = (b2.X - a2.X) * aspect, sy = b2.Y - a2.Y;
        var denominator = rx * sy - ry * sx;
        if (MathF.Abs(denominator) < 1e-9f)
            return null;
        var t = ((qx - px) * sy - (qy - py) * sx) / denominator;
        var u = ((qx - px) * ry - (qy - py) * rx) / denominator;
        if (t <= 0 || t >= 1 || u <= 0 || u >= 1)
            return null;
        return new PointF(a1.X + (b1.X - a1.X) * t, a1.Y + (b1.Y - a1.Y) * t);
    }

    /// <summary>0 or 1 by the sign of the polygon's area (shoelace), in square pixels.</summary>
    private static int SideOf(PointF[] corners, float aspect)
    {
        var area = 0f;
        for (var i = 0; i < corners.Length; i++)
        {
            var p = corners[i];
            var q = corners[(i + 1) % corners.Length];
            area += p.X * aspect * q.Y - q.X * aspect * p.Y;
        }
        return area >= 0 ? 0 : 1;
    }
}

/// <summary>A piece of a fill: four corners (normalized to the frame) and which side of the ribbon it shows.</summary>
public sealed record FillPiece(PointF[] Corners, int Side);

/// <summary>What the hands draw into the "HitCam" camera picture for one analysed frame.</summary>
public sealed record HandCameraScene(HitCamSceneDot[] Dots, HitCamSceneLine[] Lines, HitCamSceneQuad[] Quads)
{
    public static HandCameraScene Empty { get; } = new([], [], []);

    public bool IsEmpty => Dots.Length == 0 && Lines.Length == 0 && Quads.Length == 0;

    /// <summary>
    /// The elements <paramref name="settings"/> sends to the camera: fills under threads under the hands' points and
    /// skeleton lines.
    /// </summary>
    public static unsafe HandCameraScene Build(HandResult result, HandSettings settings)
    {
        var aspect = result.FrameHeight > 0 ? (float)result.FrameWidth / result.FrameHeight : 16f / 9;
        var dots = new List<HitCamSceneDot>();
        var lines = new List<HitCamSceneLine>();
        var quads = new List<HitCamSceneQuad>();
        var threads = settings.Threads ? result.Threads : [];

        if (settings.CameraFill && settings.FillOpacity > 0)
        {
            var from = HandSettings.ParseColor(settings.LeftColor);
            var to = HandSettings.ParseColor(settings.RightColor);
            var byFinger = threads.ToDictionary(t => t.Finger);
            foreach (var fill in result.Fills)
            {
                if (!byFinger.TryGetValue(fill.First, out var a) || !byFinger.TryGetValue(fill.Second, out var b))
                    continue;
                // The gradient runs from the middle of the left edge to the middle of the right edge, over all pieces.
                var fromX = (a.Left.X + b.Left.X) / 2;
                var fromY = (a.Left.Y + b.Left.Y) / 2;
                var toX = (a.Right.X + b.Right.X) / 2;
                var toY = (a.Right.Y + b.Right.Y) / 2;
                foreach (var piece in HandStyle.FillPieces(a, b, aspect))
                {
                    var (rgbFrom, rgbTo) = HandStyle.FillColors(from, to, fill.First, piece.Side);
                    var quad = new HitCamSceneQuad
                    {
                        RgbFrom = rgbFrom, RgbTo = rgbTo, Alpha = settings.FillOpacity / 100f,
                        FromX = fromX, FromY = fromY, ToX = toX, ToY = toY,
                    };
                    for (var i = 0; i < 4; i++)
                    {
                        quad.X[i] = piece.Corners[i].X;
                        quad.Y[i] = piece.Corners[i].Y;
                    }
                    quads.Add(quad);
                }
            }
        }

        if (settings.CameraThreads)
        {
            foreach (var t in threads)
                lines.Add(Line(t.Left, t.Right, HandStyle.ThreadGlowWidth, HandStyle.Thread, HandStyle.ThreadGlowAlpha));
            foreach (var t in threads)
                lines.Add(Line(t.Left, t.Right, HandStyle.ThreadWidth, HandStyle.Thread, 1));
        }

        if (settings.CameraPoints)
        {
            foreach (var hand in result.Hands)
            {
                if (hand.Points.Count < HandLandmarker.PointCount)
                    continue;
                var size = HandStyle.HandSize(hand.Points, aspect);
                if (settings.ShowSkeleton)
                {
                    foreach (var (a, b) in HandStyle.Bones)
                        lines.Add(Line(hand.Points[a], hand.Points[b], HandStyle.BoneWidth, 0xFFFFFF, HandStyle.BoneAlpha));
                }
                for (var i = 0; i < hand.Points.Count; i++)
                {
                    var radius = HandStyle.DotRadius(size, i);
                    var p = hand.Points[i];
                    // A dark ring under the coloured dot keeps it visible on any background.
                    dots.Add(new HitCamSceneDot { X = p.X, Y = p.Y, Radius = radius + HandStyle.OutlineWidth, Rgb = HandStyle.Outline });
                    dots.Add(new HitCamSceneDot { X = p.X, Y = p.Y, Radius = radius, Rgb = HandStyle.FingerColors[HandStyle.FingerOf(i)] });
                }
            }
        }

        return new HandCameraScene([.. dots], [.. lines], [.. quads]);
    }

    private static HitCamSceneLine Line(PointF a, PointF b, float width, uint rgb, float alpha) => new()
    {
        X0 = a.X, Y0 = a.Y, X1 = b.X, Y1 = b.Y, Width = width, Rgb = rgb, Alpha = alpha,
    };
}

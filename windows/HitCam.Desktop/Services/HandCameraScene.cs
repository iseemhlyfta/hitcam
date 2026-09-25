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
}

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
                var corners = HandStyle.FillCorners(a, b);
                var quad = new HitCamSceneQuad
                {
                    RgbFrom = from, RgbTo = to, Alpha = settings.FillOpacity / 100f,
                    FromX = (a.Left.X + b.Left.X) / 2, FromY = (a.Left.Y + b.Left.Y) / 2,
                    ToX = (a.Right.X + b.Right.X) / 2, ToY = (a.Right.Y + b.Right.Y) / 2,
                };
                for (var i = 0; i < 4; i++)
                {
                    quad.X[i] = corners[i].X;
                    quad.Y[i] = corners[i].Y;
                }
                quads.Add(quad);
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

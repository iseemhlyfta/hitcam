using System.Drawing;

namespace HitCam.Vision;

/// <summary>One object in one frame. <see cref="Box"/> is normalized to the frame (0..1, left/top/width/height).</summary>
public readonly record struct Detection(int ClassId, string Name, float Score, RectangleF Box);

/// <summary>What to report: minimum confidence and classes the user turned off.</summary>
public sealed record DetectionOptions
{
    public static DetectionOptions Default { get; } = new();

    /// <summary>0..1; detections below it are dropped.</summary>
    public float Threshold { get; init; } = 0.5f;

    /// <summary>Class ids never reported.</summary>
    public IReadOnlySet<int> ExcludedClasses { get; init; } = new HashSet<int>();

    /// <summary>
    /// DETR needs no NMS, but a second box of the same class overlapping more than this is dropped as a safety net.
    /// </summary>
    public float DuplicateIou { get; init; } = 0.7f;
}

/// <summary>A BGRA frame to analyse; buffers are reused between frames.</summary>
public sealed class VisionFrame
{
    private byte[] _pixels = [];

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Bytes per row: always <c>Width * 4</c>.</summary>
    public int Stride => Width * 4;

    /// <summary>Which frame of the source this is; increases with every new frame.</summary>
    public ulong Sequence { get; set; }

    /// <summary>The whole buffer; only the first <c>Stride * Height</c> bytes belong to the frame.</summary>
    public byte[] Buffer => _pixels;

    public Span<byte> Pixels => _pixels.AsSpan(0, Stride * Height);

    /// <summary>Sets the size, growing the buffer if needed (the content is undefined after).</summary>
    public void SetSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var bytes = checked(width * 4 * height);
        if (_pixels.Length < bytes)
            _pixels = GC.AllocateUninitializedArray<byte>(bytes, pinned: true);
        Width = width;
        Height = height;
    }
}

public static class Geometry
{
    /// <summary>Intersection over union; 0 for empty boxes.</summary>
    public static float Iou(RectangleF a, RectangleF b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        if (right <= left || bottom <= top)
            return 0;
        var intersection = (right - left) * (bottom - top);
        var union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union > 0 ? intersection / union : 0;
    }
}

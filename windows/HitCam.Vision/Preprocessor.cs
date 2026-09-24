namespace HitCam.Vision;

/// <summary>
/// Turns a BGRA frame of any size into the network input: the whole frame stretched to the input size (no
/// letterbox), RGB, <c>(v / 255 - mean) / std</c>, planar CHW. Resizing is plain bilinear without antialiasing, as in
/// the model's own PyTorch pipeline (<c>F.interpolate(mode="bilinear", align_corners=False)</c>, like
/// <c>cv2.INTER_LINEAR</c>): each output pixel samples the source at <c>(x + 0.5) * in / out - 0.5</c> from its 2×2
/// neighbours, with no prefilter when shrinking. An antialiased resize shifts the scores by about 0.1. Reuses its
/// buffers; not thread-safe.
/// </summary>
public sealed class Preprocessor
{
    private Taps? _horizontal;
    private Taps? _vertical;
    private float[] _rows = [];

    public void Run(
        ReadOnlySpan<byte> bgra, int width, int height, int stride,
        Span<float> destination, int inputWidth, int inputHeight,
        IReadOnlyList<float> mean, IReadOnlyList<float> std)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        if (bgra.Length < stride * (height - 1) + width * 4)
            throw new ArgumentException("The frame buffer is too small.", nameof(bgra));
        var plane = inputWidth * inputHeight;
        if (destination.Length < plane * 3)
            throw new ArgumentException("The input tensor is too small.", nameof(destination));

        var horizontal = _horizontal is { } h && h.InSize == width && h.OutSize == inputWidth ? h : _horizontal = new Taps(width, inputWidth);
        var vertical = _vertical is { } v && v.InSize == height && v.OutSize == inputHeight ? v : _vertical = new Taps(height, inputHeight);

        // Pass 1: the source rows the output needs, resized horizontally, as interleaved RGB floats in 0..255.
        // Plain bilinear touches at most two source rows per output row, so most rows of a large frame are skipped.
        var rowFloats = inputWidth * 3;
        if (_rows.Length < rowFloats * height)
            _rows = new float[rowFloats * height];
        var rows = _rows.AsSpan(0, rowFloats * height);
        var lastRow = -1;
        for (var o = 0; o < inputHeight; o++)
        {
            for (var y = vertical.First[o]; y <= vertical.Second[o]; y++)
            {
                if (y <= lastRow)
                    continue;
                lastRow = y;
                var source = bgra.Slice(y * stride, width * 4);
                var target = rows.Slice(y * rowFloats, rowFloats);
                for (var x = 0; x < inputWidth; x++)
                {
                    var a = horizontal.First[x] * 4;
                    var b = horizontal.Second[x] * 4;
                    var w = horizontal.Weight[x];
                    target[x * 3] = source[a + 2] + (source[b + 2] - source[a + 2]) * w;
                    target[x * 3 + 1] = source[a + 1] + (source[b + 1] - source[a + 1]) * w;
                    target[x * 3 + 2] = source[a] + (source[b] - source[a]) * w;
                }
            }
        }

        // Pass 2: vertically, normalized, into the three planes.
        Span<float> scale = stackalloc float[3];
        Span<float> offset = stackalloc float[3];
        for (var c = 0; c < 3; c++)
        {
            scale[c] = 1f / (255f * std[c]);
            offset[c] = -mean[c] / std[c];
        }
        var red = destination[..plane];
        var green = destination.Slice(plane, plane);
        var blue = destination.Slice(plane * 2, plane);
        for (var y = 0; y < inputHeight; y++)
        {
            var top = rows.Slice(vertical.First[y] * rowFloats, rowFloats);
            var bottom = rows.Slice(vertical.Second[y] * rowFloats, rowFloats);
            var w = vertical.Weight[y];
            var output = y * inputWidth;
            for (var x = 0; x < inputWidth; x++)
            {
                var p = x * 3;
                var r = top[p] + (bottom[p] - top[p]) * w;
                var g = top[p + 1] + (bottom[p + 1] - top[p + 1]) * w;
                var b = top[p + 2] + (bottom[p + 2] - top[p + 2]) * w;
                red[output + x] = r * scale[0] + offset[0];
                green[output + x] = g * scale[1] + offset[1];
                blue[output + x] = b * scale[2] + offset[2];
            }
        }
    }

    /// <summary>For each output position along one axis: the two source pixels and the weight of the second.</summary>
    private sealed class Taps
    {
        public Taps(int inSize, int outSize)
        {
            InSize = inSize;
            OutSize = outSize;
            First = new int[outSize];
            Second = new int[outSize];
            Weight = new float[outSize];
            var scale = (double)inSize / outSize;
            for (var o = 0; o < outSize; o++)
            {
                // Half-pixel centers; before the first pixel's center the edge pixel is used (as PyTorch does).
                var source = Math.Max((o + 0.5) * scale - 0.5, 0);
                var first = Math.Min((int)source, inSize - 1);
                First[o] = first;
                Second[o] = Math.Min(first + 1, inSize - 1);
                Weight[o] = (float)(source - first);
            }
        }

        public int InSize { get; }

        public int OutSize { get; }

        public int[] First { get; }

        public int[] Second { get; }

        public float[] Weight { get; }
    }
}

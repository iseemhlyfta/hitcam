namespace HitCam.Vision;

/// <summary>
/// Turns a BGRA frame of any size into the network input: the whole frame stretched to the input size (no
/// letterbox), RGB, <c>(v / 255 - mean) / std</c>, planar CHW. Resizing is bilinear with antialiasing when
/// shrinking, like PIL's <c>Image.resize(BILINEAR)</c> the model was checked against. Reuses its buffers; not
/// thread-safe.
/// </summary>
public sealed class Preprocessor
{
    private Coefficients? _horizontal;
    private Coefficients? _vertical;
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

        var horizontal = _horizontal is { } h && h.InSize == width && h.OutSize == inputWidth ? h : _horizontal = new Coefficients(width, inputWidth);
        var vertical = _vertical is { } v && v.InSize == height && v.OutSize == inputHeight ? v : _vertical = new Coefficients(height, inputHeight);

        // Pass 1: every source row resized horizontally, as interleaved RGB floats in 0..255.
        var rowFloats = inputWidth * 3;
        if (_rows.Length < rowFloats * height)
            _rows = new float[rowFloats * height];
        var rows = _rows.AsSpan(0, rowFloats * height);
        for (var y = 0; y < height; y++)
        {
            var source = bgra.Slice(y * stride, width * 4);
            var target = rows.Slice(y * rowFloats, rowFloats);
            for (var x = 0; x < inputWidth; x++)
            {
                var start = horizontal.Start[x];
                var weights = horizontal.WeightsOf(x);
                float r = 0, g = 0, b = 0;
                for (var k = 0; k < weights.Length; k++)
                {
                    var p = (start + k) * 4;
                    var w = weights[k];
                    b += source[p] * w;
                    g += source[p + 1] * w;
                    r += source[p + 2] * w;
                }
                target[x * 3] = r;
                target[x * 3 + 1] = g;
                target[x * 3 + 2] = b;
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
            var start = vertical.Start[y];
            var weights = vertical.WeightsOf(y);
            var output = y * inputWidth;
            for (var x = 0; x < inputWidth; x++)
            {
                float r = 0, g = 0, b = 0;
                var column = x * 3;
                for (var k = 0; k < weights.Length; k++)
                {
                    var p = (start + k) * rowFloats + column;
                    var w = weights[k];
                    r += rows[p] * w;
                    g += rows[p + 1] * w;
                    b += rows[p + 2] * w;
                }
                red[output + x] = r * scale[0] + offset[0];
                green[output + x] = g * scale[1] + offset[1];
                blue[output + x] = b * scale[2] + offset[2];
            }
        }
    }

    /// <summary>Triangle filter taps for one axis, widened by the shrink factor (antialiasing).</summary>
    private sealed class Coefficients
    {
        private readonly float[] _weights;
        private readonly int[] _count;
        private readonly int _taps;

        public Coefficients(int inSize, int outSize)
        {
            InSize = inSize;
            OutSize = outSize;
            var scale = (double)inSize / outSize;
            var filterScale = Math.Max(scale, 1.0);
            var support = filterScale;
            _taps = (int)Math.Ceiling(support) * 2 + 1;
            Start = new int[outSize];
            _count = new int[outSize];
            _weights = new float[outSize * _taps];
            for (var o = 0; o < outSize; o++)
            {
                var center = (o + 0.5) * scale;
                var min = Math.Max((int)(center - support + 0.5), 0);
                var max = Math.Min((int)(center + support + 0.5), inSize);
                var count = Math.Min(max - min, _taps);
                double sum = 0;
                for (var k = 0; k < count; k++)
                {
                    var w = Math.Max(0, 1 - Math.Abs((k + min - center + 0.5) / filterScale));
                    _weights[o * _taps + k] = (float)w;
                    sum += w;
                }
                if (sum > 0)
                {
                    for (var k = 0; k < count; k++)
                        _weights[o * _taps + k] = (float)(_weights[o * _taps + k] / sum);
                }
                else
                {
                    // Cannot happen for sane sizes; take the nearest pixel.
                    count = 1;
                    min = Math.Clamp((int)center, 0, inSize - 1);
                    _weights[o * _taps] = 1;
                }
                Start[o] = min;
                _count[o] = count;
            }
        }

        public int InSize { get; }

        public int OutSize { get; }

        public int[] Start { get; }

        public ReadOnlySpan<float> WeightsOf(int output) => _weights.AsSpan(output * _taps, _count[output]);
    }
}

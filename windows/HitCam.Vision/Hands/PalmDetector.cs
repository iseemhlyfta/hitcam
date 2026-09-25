using System.Drawing;
using Microsoft.ML.OnnxRuntime;

namespace HitCam.Vision.Hands;

/// <summary>
/// MediaPipe's palm detector (192×192, 2016 anchors). Finds palms rather than whole hands: a palm is nearly rigid and
/// square, so it is found at any finger pose. Not thread-safe: one thread runs it.
/// </summary>
public sealed class PalmDetector : IDisposable
{
    public const int InputSize = 192;
    private const int Values = 18; // box (cx, cy, w, h) and 7 keypoints (x, y), in input pixels

    private static readonly PointF[] Anchors = HandGeometry.PalmAnchors();

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly float[] _input = new float[InputSize * InputSize * 3];
    private readonly OrtValue _inputValue;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private Sampler? _sampler;

    private PalmDetector(InferenceSession session, string provider)
    {
        _session = session;
        Provider = provider;
        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, [1, InputSize, InputSize, 3]);
        _inputNames = [session.InputMetadata.Keys.First()];
        // [1, 2016, 18] boxes and keypoints, then [1, 2016, 1] score logits.
        _outputNames = [.. session.OutputMetadata.OrderByDescending(o => o.Value.Dimensions[^1]).Select(o => o.Key)];
        if (_outputNames.Length != 2 || session.OutputMetadata[_outputNames[0]].Dimensions[^1] != Values)
            throw new ModelFormatException("the palm detector must have two outputs (boxes, scores)");
    }

    public string Provider { get; }

    public static PalmDetector Load(string path, ProviderPreference preference = ProviderPreference.Auto)
    {
        var (session, provider) = OnnxLoader.Load(path, preference, WarmUp);
        try
        {
            return new PalmDetector(session, provider);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static void WarmUp(InferenceSession session)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(new float[InputSize * InputSize * 3], [1, InputSize, InputSize, 3]);
        using var runOptions = new RunOptions();
        using var _ = session.Run(runOptions, [session.InputMetadata.Keys.First()], [input], [.. session.OutputMetadata.Keys]);
    }

    /// <summary>Palms at least <paramref name="threshold"/> sure, best first, in pixels of the frame.</summary>
    public List<Palm> Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride, float threshold = 0.5f)
    {
        var sampler = _sampler is { } s && s.Width == width && s.Height == height ? s : _sampler = new Sampler(width, height);
        sampler.Run(bgra, stride, _input);
        lock (OnnxGate.Lock)
        {
            using var outputs = _session.Run(_runOptions, _inputNames, [_inputValue], _outputNames);
            return Decode(outputs[0].GetTensorDataAsSpan<float>(), outputs[1].GetTensorDataAsSpan<float>(), sampler, threshold);
        }
    }

    /// <summary>Raw outputs to palms in the frame.</summary>
    internal static List<Palm> Decode(ReadOnlySpan<float> raw, ReadOnlySpan<float> scores, Sampler letterbox, float threshold)
    {
        if (raw.Length < Anchors.Length * Values || scores.Length < Anchors.Length)
            throw new ModelFormatException("unexpected palm detector output size");
        var candidates = new List<Palm>();
        for (var i = 0; i < Anchors.Length; i++)
        {
            var score = 1f / (1f + MathF.Exp(-Math.Clamp(scores[i], -100f, 100f)));
            if (score < threshold)
                continue;
            var v = raw.Slice(i * Values, Values);
            var anchor = Anchors[i];
            var cx = anchor.X + v[0] / InputSize;
            var cy = anchor.Y + v[1] / InputSize;
            var w = v[2] / InputSize;
            var h = v[3] / InputSize;
            var topLeft = letterbox.ToFrame(cx - w / 2, cy - h / 2);
            var bottomRight = letterbox.ToFrame(cx + w / 2, cy + h / 2);
            var keypoints = new PointF[7];
            for (var k = 0; k < 7; k++)
                keypoints[k] = letterbox.ToFrame(anchor.X + v[4 + k * 2] / InputSize, anchor.Y + v[5 + k * 2] / InputSize);
            candidates.Add(new Palm(RectangleF.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y), keypoints, score));
        }
        return HandGeometry.WeightedNms(candidates);
    }

    public void Dispose()
    {
        lock (OnnxGate.Lock)
        {
            _inputValue.Dispose();
            _runOptions.Dispose();
            _session.Dispose();
        }
    }

    /// <summary>
    /// The frame fitted into the 192×192 input with black bars (letterbox), RGB in 0..1, NHWC. Each input pixel
    /// averages up to 4×4 samples of its footprint, so a 1080p frame shrunk 10 times does not flicker.
    /// </summary>
    internal sealed class Sampler
    {
        private const int MaxSamples = 4;
        private readonly int[] _xs;
        private readonly int[] _ys;
        private readonly int _samples;

        public Sampler(int width, int height)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
            Width = width;
            Height = height;
            Scale = (float)InputSize / Math.Max(width, height);
            PadX = (InputSize - width * Scale) / 2;
            PadY = (InputSize - height * Scale) / 2;
            _samples = Math.Clamp((int)MathF.Ceiling(1 / Scale), 1, MaxSamples);
            _xs = Taps(width, PadX);
            _ys = Taps(height, PadY);
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Input pixels per frame pixel.</summary>
        public float Scale { get; }

        public float PadX { get; }

        public float PadY { get; }

        /// <summary>A point of the input (0..1) in frame pixels.</summary>
        public PointF ToFrame(float x, float y) => new((x * InputSize - PadX) / Scale, (y * InputSize - PadY) / Scale);

        /// <summary>For every input position, the frame pixels it samples; -1 in the black bars.</summary>
        private int[] Taps(int size, float pad)
        {
            var taps = new int[InputSize * _samples];
            for (var o = 0; o < InputSize; o++)
            {
                var start = (o - pad) / Scale;
                var end = (o + 1 - pad) / Scale;
                var center = (start + end) / 2;
                for (var i = 0; i < _samples; i++)
                {
                    var position = start + (i + 0.5f) * (end - start) / _samples;
                    taps[o * _samples + i] = center < 0 || center >= size ? -1 : Math.Clamp((int)position, 0, size - 1);
                }
            }
            return taps;
        }

        public void Run(ReadOnlySpan<byte> bgra, int stride, Span<float> destination)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(stride, Width * 4);
            if (bgra.Length < stride * (Height - 1) + Width * 4)
                throw new ArgumentException("The frame buffer is too small.", nameof(bgra));
            var n = _samples;
            var weight = 1f / (255f * n * n);
            for (var oy = 0; oy < InputSize; oy++)
            {
                var row = destination.Slice(oy * InputSize * 3, InputSize * 3);
                if (_ys[oy * n] < 0)
                {
                    row.Clear();
                    continue;
                }
                for (var ox = 0; ox < InputSize; ox++)
                {
                    if (_xs[ox * n] < 0)
                    {
                        row[ox * 3] = row[ox * 3 + 1] = row[ox * 3 + 2] = 0;
                        continue;
                    }
                    int r = 0, g = 0, b = 0;
                    for (var sy = 0; sy < n; sy++)
                    {
                        var line = bgra.Slice(_ys[oy * n + sy] * stride);
                        for (var sx = 0; sx < n; sx++)
                        {
                            var p = _xs[ox * n + sx] * 4;
                            b += line[p];
                            g += line[p + 1];
                            r += line[p + 2];
                        }
                    }
                    row[ox * 3] = r * weight;
                    row[ox * 3 + 1] = g * weight;
                    row[ox * 3 + 2] = b * weight;
                }
            }
        }
    }
}

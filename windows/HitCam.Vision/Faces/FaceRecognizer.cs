using System.Drawing;
using HitCam.Vision.Hands;
using Microsoft.ML.OnnxRuntime;

namespace HitCam.Vision.Faces;

/// <summary>
/// SFace (OpenCV Zoo, Apache 2.0): a face "fingerprint" of 128 numbers. The face is first aligned by its 5 landmarks
/// onto the model's template (a similarity transform, as OpenCV's FaceRecognizerSF.alignCrop does), cropped to
/// 112×112, RGB 0..255. Fingerprints are unit length: their dot product is the cosine similarity, and the same person
/// scores above <see cref="SameFace"/>. Not thread-safe.
/// </summary>
public sealed class FaceRecognizer : IDisposable
{
    public const int InputSize = 112;
    public const int Length = 128;

    /// <summary>Cosine similarity from which two fingerprints are the same person (OpenCV's threshold).</summary>
    public const float SameFace = 0.363f;

    /// <summary>Where SFace expects the 5 landmarks in its 112×112 input.</summary>
    public static readonly PointF[] Template =
    [
        new(38.2946f, 51.6963f), new(73.5318f, 51.5014f), new(56.0252f, 71.7366f), new(41.5493f, 92.3655f), new(70.7299f, 92.2041f),
    ];

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions;
    private readonly float[] _input = new float[3 * InputSize * InputSize];
    private readonly OrtValue _inputValue;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;

    private FaceRecognizer(InferenceSession session, string provider)
    {
        _session = session;
        Provider = provider;
        _inputNames = [session.InputMetadata.Keys.First()];
        _outputNames = [session.OutputMetadata.Keys.First()];
        if (session.OutputMetadata.Values.First().Dimensions[^1] != Length)
            throw new ModelFormatException("the face recognizer must output 128 numbers");
        // Native objects only once the model is accepted: nothing is left for the finalizer to release off the gate.
        _runOptions = new RunOptions();
        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, [1, 3, InputSize, InputSize]);
    }

    public string Provider { get; }

    public static FaceRecognizer Load(string path, ProviderPreference preference = ProviderPreference.Auto)
    {
        var (session, provider) = OnnxLoader.Load(path, preference, WarmUp);
        try
        {
            return new FaceRecognizer(session, provider);
        }
        catch
        {
            lock (OnnxGate.Lock)
                session.Dispose();
            throw;
        }
    }

    private static void WarmUp(InferenceSession session)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(new float[3 * InputSize * InputSize], [1, 3, InputSize, InputSize]);
        using var runOptions = new RunOptions();
        using var _ = session.Run(runOptions, [session.InputMetadata.Keys.First()], [input], [.. session.OutputMetadata.Keys]);
    }

    /// <summary>The fingerprint of the face with these landmarks (pixels of the frame).</summary>
    public float[] Fingerprint(ReadOnlySpan<byte> bgra, int width, int height, int stride, IReadOnlyList<PointF> landmarks)
    {
        Align(bgra, width, height, stride, Similarity(landmarks, Template), _input);
        float[] result;
        lock (OnnxGate.Lock)
        {
            using var outputs = _session.Run(_runOptions, _inputNames, [_inputValue], _outputNames);
            result = outputs[0].GetTensorDataAsSpan<float>().ToArray();
        }
        var length = MathF.Sqrt(result.Sum(v => v * v));
        if (length > 0)
        {
            for (var i = 0; i < result.Length; i++)
                result[i] /= length;
        }
        return result;
    }

    public static float Similarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = 0f;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            sum += a[i] * b[i];
        return sum;
    }

    /// <summary>
    /// The similarity transform (rotation, uniform scale, shift) that best maps <paramref name="from"/> onto
    /// <paramref name="to"/> in the least-squares sense (Umeyama): [a -b tx; b a ty] as (a, b, tx, ty).
    /// </summary>
    public static (float A, float B, float Tx, float Ty) Similarity(IReadOnlyList<PointF> from, IReadOnlyList<PointF> to)
    {
        var n = Math.Min(from.Count, to.Count);
        float fx = 0, fy = 0, tx = 0, ty = 0;
        for (var i = 0; i < n; i++)
        {
            fx += from[i].X; fy += from[i].Y;
            tx += to[i].X; ty += to[i].Y;
        }
        fx /= n; fy /= n; tx /= n; ty /= n;
        // For a similarity, the least-squares solution is a = Σ(u·v)/Σ|u|², b = Σ(u×v)/Σ|u|² (u from, v to, centred).
        float dot = 0, cross = 0, norm = 0;
        for (var i = 0; i < n; i++)
        {
            var ux = from[i].X - fx;
            var uy = from[i].Y - fy;
            var vx = to[i].X - tx;
            var vy = to[i].Y - ty;
            dot += ux * vx + uy * vy;
            cross += ux * vy - uy * vx;
            norm += ux * ux + uy * uy;
        }
        if (norm <= 0)
            return (1, 0, tx - fx, ty - fy);
        var a = dot / norm;
        var b = cross / norm;
        return (a, b, tx - (a * fx - b * fy), ty - (b * fx + a * fy));
    }

    /// <summary>
    /// The 112×112 aligned face: each input pixel takes the frame at the inverse transform, bilinear, black outside
    /// (like cv::warpAffine); RGB 0..255, planar.
    /// </summary>
    internal static void Align(ReadOnlySpan<byte> bgra, int width, int height, int stride, (float A, float B, float Tx, float Ty) m, Span<float> destination)
    {
        // Inverse of [a -b; b a]: [a b; -b a] / (a² + b²).
        var det = m.A * m.A + m.B * m.B;
        if (det <= 0)
        {
            destination.Clear();
            return;
        }
        var ia = m.A / det;
        var ib = m.B / det;
        const int plane = InputSize * InputSize;
        for (var y = 0; y < InputSize; y++)
        {
            for (var x = 0; x < InputSize; x++)
            {
                var dx = x - m.Tx;
                var dy = y - m.Ty;
                var sx = ia * dx + ib * dy;
                var sy = -ib * dx + ia * dy;
                float r = 0, g = 0, b = 0;
                var x0 = (int)MathF.Floor(sx);
                var y0 = (int)MathF.Floor(sy);
                var wx = sx - x0;
                var wy = sy - y0;
                for (var j = 0; j < 2; j++)
                {
                    var py = y0 + j;
                    if (py < 0 || py >= height)
                        continue;
                    for (var i = 0; i < 2; i++)
                    {
                        var px = x0 + i;
                        if (px < 0 || px >= width)
                            continue;
                        var w = (i == 0 ? 1 - wx : wx) * (j == 0 ? 1 - wy : wy);
                        var p = py * stride + px * 4;
                        b += bgra[p] * w;
                        g += bgra[p + 1] * w;
                        r += bgra[p + 2] * w;
                    }
                }
                var o = y * InputSize + x;
                destination[o] = r;
                destination[plane + o] = g;
                destination[2 * plane + o] = b;
            }
        }
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
}

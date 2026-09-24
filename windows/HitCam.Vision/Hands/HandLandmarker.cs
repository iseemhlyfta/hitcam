using System.Drawing;
using Microsoft.ML.OnnxRuntime;

namespace HitCam.Vision.Hands;

/// <summary>
/// The 21 points of one hand, in pixels of the frame, MediaPipe's order: 0 the wrist; 1–4 the thumb, 5–8 the index
/// finger, 9–12 the middle finger, 13–16 the ring finger, 17–20 the little finger (base to tip).
/// </summary>
/// <param name="Score">How sure the model is that the crop holds a hand, 0..1.</param>
/// <param name="Handedness">0..1: the chance it is a right hand as the model sees it (a mirrored picture swaps them).</param>
public sealed record HandLandmarks(PointF[] Points, float Score, float Handedness);

/// <summary>
/// MediaPipe's hand landmark model (224×224 crop turned upright, see <see cref="HandRoi"/>). Not thread-safe: one
/// thread runs it.
/// </summary>
public sealed class HandLandmarker : IDisposable
{
    public const int InputSize = 224;
    public const int PointCount = 21;

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly float[] _input = new float[InputSize * InputSize * 3];
    private readonly OrtValue _inputValue;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;

    private HandLandmarker(InferenceSession session, string provider)
    {
        _session = session;
        Provider = provider;
        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, [1, InputSize, InputSize, 3]);
        _inputNames = [session.InputMetadata.Keys.First()];
        // Landmarks [1, 63], presence score [1, 1], handedness [1, 1] (world landmarks [1, 63] are not used).
        var outputs = session.OutputMetadata.ToList();
        if (outputs.Count < 3 || outputs[0].Value.Dimensions[^1] != PointCount * 3
            || outputs[1].Value.Dimensions[^1] != 1 || outputs[2].Value.Dimensions[^1] != 1)
            throw new ModelFormatException("the hand landmark model must output landmarks, score and handedness");
        _outputNames = [outputs[0].Key, outputs[1].Key, outputs[2].Key];
    }

    public string Provider { get; }

    public static HandLandmarker Load(string path, ProviderPreference preference = ProviderPreference.Auto)
    {
        var (session, provider) = OnnxLoader.Load(path, preference, WarmUp);
        try
        {
            return new HandLandmarker(session, provider);
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
        using var _ = session.Run(new RunOptions(), [session.InputMetadata.Keys.First()], [input], [.. session.OutputMetadata.Keys]);
    }

    public HandLandmarks Run(ReadOnlySpan<byte> bgra, int width, int height, int stride, HandRoi roi)
    {
        Crop(bgra, width, height, stride, roi, _input);
        using var outputs = _session.Run(_runOptions, _inputNames, [_inputValue], _outputNames);
        var raw = outputs[0].GetTensorDataAsSpan<float>();
        var points = new PointF[PointCount];
        for (var i = 0; i < PointCount; i++)
            points[i] = roi.ToFrame(raw[i * 3] / InputSize - 0.5f, raw[i * 3 + 1] / InputSize - 0.5f);
        return new HandLandmarks(points, outputs[1].GetTensorDataAsSpan<float>()[0], outputs[2].GetTensorDataAsSpan<float>()[0]);
    }

    /// <summary>
    /// The turned square <paramref name="roi"/> of the frame, as the 224×224 input: RGB in 0..1, NHWC, black outside
    /// the frame. One bilinear sample per input pixel, as MediaPipe's own crop (OpenCV warpAffine, INTER_LINEAR): the
    /// model was trained on such crops, and the preview is small enough that hands are rarely shrunk more than 3 times.
    /// </summary>
    public static void Crop(ReadOnlySpan<byte> bgra, int width, int height, int stride, HandRoi roi, Span<float> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        if (bgra.Length < stride * (height - 1) + width * 4)
            throw new ArgumentException("The frame buffer is too small.", nameof(bgra));
        if (destination.Length < InputSize * InputSize * 3)
            throw new ArgumentException("The input tensor is too small.", nameof(destination));

        var step = roi.Size / InputSize; // frame pixels per input pixel
        var (sin, cos) = MathF.SinCos(roi.Rotation);
        float ux = cos * step, uy = sin * step, vx = -sin * step, vy = cos * step;
        // Frame position of input pixel (0, 0)'s center, as a pixel index (pixel centers are at i + 0.5).
        var corner = roi.ToFrame(-0.5f, -0.5f);
        var originX = corner.X + 0.5f * (ux + vx) - 0.5f;
        var originY = corner.Y + 0.5f * (uy + vy) - 0.5f;
        const float scale = 1 / 255f;

        for (var v = 0; v < InputSize; v++)
        {
            var rowX = originX + v * vx;
            var rowY = originY + v * vy;
            var output = destination.Slice(v * InputSize * 3, InputSize * 3);
            for (var u = 0; u < InputSize; u++)
            {
                var x = rowX + u * ux;
                var y = rowY + u * uy;
                var x0 = (int)MathF.Floor(x);
                var y0 = (int)MathF.Floor(y);
                float r, g, b;
                if (x0 >= 0 && y0 >= 0 && x0 + 1 < width && y0 + 1 < height)
                {
                    // Inside: the four neighbours without bounds checks per tap.
                    var fx = x - x0;
                    var fy = y - y0;
                    var p = y0 * stride + x0 * 4;
                    var q = p + stride;
                    float w00 = (1 - fx) * (1 - fy), w01 = fx * (1 - fy), w10 = (1 - fx) * fy, w11 = fx * fy;
                    b = bgra[p] * w00 + bgra[p + 4] * w01 + bgra[q] * w10 + bgra[q + 4] * w11;
                    g = bgra[p + 1] * w00 + bgra[p + 5] * w01 + bgra[q + 1] * w10 + bgra[q + 5] * w11;
                    r = bgra[p + 2] * w00 + bgra[p + 6] * w01 + bgra[q + 2] * w10 + bgra[q + 6] * w11;
                }
                else
                {
                    r = g = b = 0;
                    SampleEdge(bgra, width, height, stride, x, y, ref r, ref g, ref b);
                }
                output[u * 3] = r * scale;
                output[u * 3 + 1] = g * scale;
                output[u * 3 + 2] = b * scale;
            }
        }
    }

    /// <summary>Bilinear sample at the frame's edge or outside it; pixels outside the frame are black.</summary>
    private static void SampleEdge(ReadOnlySpan<byte> bgra, int width, int height, int stride, float x, float y, ref float r, ref float g, ref float b)
    {
        if (x <= -1 || y <= -1 || x >= width || y >= height)
            return;
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        for (var dy = 0; dy < 2; dy++)
        {
            var py = y0 + dy;
            if (py < 0 || py >= height)
                continue;
            var wy = dy == 0 ? 1 - fy : fy;
            for (var dx = 0; dx < 2; dx++)
            {
                var px = x0 + dx;
                if (px < 0 || px >= width)
                    continue;
                var w = wy * (dx == 0 ? 1 - fx : fx);
                var p = py * stride + px * 4;
                b += bgra[p] * w;
                g += bgra[p + 1] * w;
                r += bgra[p + 2] * w;
            }
        }
    }

    public void Dispose()
    {
        _inputValue.Dispose();
        _runOptions.Dispose();
        _session.Dispose();
    }
}

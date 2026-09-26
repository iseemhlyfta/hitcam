using System.Drawing;
using HitCam.Vision.Hands;
using Microsoft.ML.OnnxRuntime;

namespace HitCam.Vision.Faces;

/// <summary>
/// A face in pixels of the frame. <see cref="Landmarks"/>: right eye, left eye, nose tip, right and left corners of
/// the mouth (as seen in the picture: the first is on the left).
/// </summary>
public sealed record DetectedFace(RectangleF Box, PointF[] Landmarks, float Score);

/// <summary>
/// YuNet (OpenCV Zoo, MIT): an anchor-free face detector with 5 landmarks. The frame is fitted into the 640×640 input
/// at the top-left (the rest black), BGR 0..255, NCHW; outputs per stride 8, 16, 32 are decoded as OpenCV's
/// FaceDetectorYN does. Not thread-safe: one thread runs it.
/// </summary>
public sealed class FaceDetector : IDisposable
{
    public const int InputSize = 640;
    private static readonly int[] Strides = [8, 16, 32];

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly float[] _input = new float[3 * InputSize * InputSize];
    private readonly OrtValue _inputValue;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;

    private FaceDetector(InferenceSession session, string provider)
    {
        _session = session;
        Provider = provider;
        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, [1, 3, InputSize, InputSize]);
        _inputNames = [session.InputMetadata.Keys.First()];
        // cls, obj, bbox, kps for each stride, in that order.
        _outputNames = [.. new[] { "cls", "obj", "bbox", "kps" }.SelectMany(kind => Strides.Select(s => $"{kind}_{s}"))];
        if (_outputNames.Any(n => !session.OutputMetadata.ContainsKey(n)))
            throw new ModelFormatException("the face detector must have the YuNet outputs cls/obj/bbox/kps_8/16/32");
    }

    public string Provider { get; }

    public static FaceDetector Load(string path, ProviderPreference preference = ProviderPreference.Auto)
    {
        var (session, provider) = OnnxLoader.Load(path, preference, WarmUp);
        try
        {
            return new FaceDetector(session, provider);
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

    /// <summary>Faces at least <paramref name="threshold"/> sure, in pixels of the frame.</summary>
    public List<DetectedFace> Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride, float threshold = 0.6f)
    {
        var scale = Fit(bgra, width, height, stride, _input);
        lock (OnnxGate.Lock)
        {
            using var outputs = _session.Run(_runOptions, _inputNames, [_inputValue], _outputNames);
            var faces = new List<DetectedFace>();
            for (var s = 0; s < Strides.Length; s++)
            {
                Decode(Strides[s], outputs[s].GetTensorDataAsSpan<float>(), outputs[3 + s].GetTensorDataAsSpan<float>(),
                    outputs[6 + s].GetTensorDataAsSpan<float>(), outputs[9 + s].GetTensorDataAsSpan<float>(), 1 / scale, threshold, faces);
            }
            return Nms(faces, 0.3f);
        }
    }

    /// <summary>
    /// One stride's outputs to faces: score √(cls·obj), box centre (cell + offset) × stride, size e^v × stride,
    /// landmarks (cell + offset) × stride; everything then scaled back to the frame.
    /// </summary>
    internal static void Decode(int stride, ReadOnlySpan<float> cls, ReadOnlySpan<float> obj, ReadOnlySpan<float> bbox,
        ReadOnlySpan<float> kps, float toFrame, float threshold, List<DetectedFace> faces)
    {
        var cols = InputSize / stride;
        var cells = cols * cols;
        for (var i = 0; i < cells; i++)
        {
            var score = MathF.Sqrt(Math.Clamp(cls[i], 0f, 1f) * Math.Clamp(obj[i], 0f, 1f));
            if (score < threshold)
                continue;
            var row = i / cols;
            var col = i % cols;
            var cx = (col + bbox[i * 4]) * stride;
            var cy = (row + bbox[i * 4 + 1]) * stride;
            var w = MathF.Exp(bbox[i * 4 + 2]) * stride;
            var h = MathF.Exp(bbox[i * 4 + 3]) * stride;
            var landmarks = new PointF[5];
            for (var n = 0; n < 5; n++)
                landmarks[n] = new PointF((kps[i * 10 + n * 2] + col) * stride * toFrame, (kps[i * 10 + n * 2 + 1] + row) * stride * toFrame);
            faces.Add(new DetectedFace(new RectangleF((cx - w / 2) * toFrame, (cy - h / 2) * toFrame, w * toFrame, h * toFrame), landmarks, score));
        }
    }

    /// <summary>Plain NMS: best first, dropping boxes that overlap a kept one more than <paramref name="iou"/>.</summary>
    internal static List<DetectedFace> Nms(List<DetectedFace> faces, float iou)
    {
        var kept = new List<DetectedFace>();
        foreach (var face in faces.OrderByDescending(f => f.Score))
        {
            if (kept.All(k => Geometry.Iou(k.Box, face.Box) <= iou))
                kept.Add(face);
        }
        return kept;
    }

    /// <summary>
    /// The frame scaled to fit 640×640, at the top-left, black elsewhere: BGR 0..255, planar. Bilinear, sampling at
    /// half-pixel centres like cv::resize. Returns the scale (input pixels per frame pixel).
    /// </summary>
    internal static float Fit(ReadOnlySpan<byte> bgra, int width, int height, int stride, Span<float> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        if (bgra.Length < stride * (height - 1) + width * 4)
            throw new ArgumentException("The frame buffer is too small.", nameof(bgra));
        var scale = (float)InputSize / Math.Max(width, height);
        var outWidth = Math.Min(InputSize, (int)(width * scale));
        var outHeight = Math.Min(InputSize, (int)(height * scale));
        const int plane = InputSize * InputSize;
        destination[..(3 * plane)].Clear();
        var sx = (float)width / outWidth;
        var sy = (float)height / outHeight;
        for (var y = 0; y < outHeight; y++)
        {
            var fy = Math.Max((y + 0.5f) * sy - 0.5f, 0);
            var y0 = Math.Min((int)fy, height - 1);
            var y1 = Math.Min(y0 + 1, height - 1);
            var wy = fy - y0;
            var row0 = bgra.Slice(y0 * stride);
            var row1 = bgra.Slice(y1 * stride);
            for (var x = 0; x < outWidth; x++)
            {
                var fx = Math.Max((x + 0.5f) * sx - 0.5f, 0);
                var x0 = Math.Min((int)fx, width - 1);
                var x1 = Math.Min(x0 + 1, width - 1);
                var wx = fx - x0;
                var o = y * InputSize + x;
                for (var c = 0; c < 3; c++)
                {
                    var top = row0[x0 * 4 + c] + (row0[x1 * 4 + c] - row0[x0 * 4 + c]) * wx;
                    var bottom = row1[x0 * 4 + c] + (row1[x1 * 4 + c] - row1[x0 * 4 + c]) * wx;
                    destination[c * plane + o] = top + (bottom - top) * wy;   // B, G, R planes
                }
            }
        }
        return scale;
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

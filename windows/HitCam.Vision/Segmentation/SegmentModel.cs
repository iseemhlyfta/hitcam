using HitCam.Vision.Hands;
using Microsoft.ML.OnnxRuntime;

namespace HitCam.Vision.Segmentation;

/// <summary>Where the segmentation model is: <c>models/segment</c> next to the app or in <c>%LOCALAPPDATA%/HitCam/models/segment</c>.</summary>
public static class SegmentModelFiles
{
    public const string FileName = "selfie_segmentation.onnx";
    public const string SubDirectory = "segment";

    public static IReadOnlyList<string> DefaultDirectories =>
        [.. ModelCatalog.DefaultDirectories.Select(d => Path.Combine(d, SubDirectory))];

    /// <summary>The model in the first folder that has it, or null.</summary>
    public static string? Find(IEnumerable<string>? directories = null) =>
        (directories ?? DefaultDirectories).Select(d => Path.Combine(d, FileName)).FirstOrDefault(File.Exists);
}

/// <summary>A person segmentation model; the engine only knows this, so tests can run it without models.</summary>
public interface ISegmentModel : IDisposable
{
    /// <summary>"DirectML" or "CPU".</summary>
    string Provider { get; }

    /// <summary>Side of the square mask, in mask pixels.</summary>
    int Size { get; }

    /// <summary>
    /// How sure the model is that each pixel is a person, 0..1, row by row: the whole frame stretched to
    /// <see cref="Size"/>×<see cref="Size"/> (no letterbox), so mask coordinates are frame coordinates scaled.
    /// </summary>
    void Segment(VisionFrame frame, Span<float> mask);
}

/// <summary>
/// MediaPipe Selfie Segmentation (Google, Apache 2.0; ONNX by onnx-community): RGB 256×256 in 0..1, planar, out a
/// person probability per pixel. About 2–3 ms with DirectML, 4 ms on the processor. Not thread-safe: one thread runs it.
/// </summary>
public sealed class SelfieSegmenter : ISegmentModel
{
    public const int InputSize = 256;
    private static readonly float[] Mean = [0, 0, 0];
    private static readonly float[] Std = [1, 1, 1];

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions;
    private readonly Preprocessor _preprocessor = new();
    private readonly float[] _input = new float[3 * InputSize * InputSize];
    private readonly OrtValue _inputValue;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;

    private SelfieSegmenter(InferenceSession session, string provider)
    {
        _session = session;
        Provider = provider;
        var (inputName, input) = session.InputMetadata.First();
        var (outputName, output) = session.OutputMetadata.First();
        if (input.Dimensions.Length != 4 || input.Dimensions[1] != 3 || output.Dimensions[^1] != InputSize || output.Dimensions[^2] != InputSize)
            throw new ModelFormatException("the segmentation model must take [1,3,256,256] and give a 256×256 mask");
        _inputNames = [inputName];
        _outputNames = [outputName];
        // Native objects only once the model is accepted: nothing is left for the finalizer to release off the gate.
        _runOptions = new RunOptions();
        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, [1, 3, InputSize, InputSize]);
    }

    public string Provider { get; }

    public int Size => InputSize;

    public static SelfieSegmenter Load(string path, ProviderPreference preference = ProviderPreference.Auto)
    {
        var (session, provider) = OnnxLoader.Load(path, preference, WarmUp);
        try
        {
            return new SelfieSegmenter(session, provider);
        }
        catch
        {
            lock (OnnxGate.Lock)
                session.Dispose();
            throw;
        }
    }

    /// <summary>Loads the model from the default folders; <see cref="FileNotFoundException"/> if it is missing.</summary>
    public static SelfieSegmenter LoadDefault(ProviderPreference preference = ProviderPreference.Auto) =>
        SegmentModelFiles.Find() is { } path
            ? Load(path, preference)
            : throw new FileNotFoundException(
                $"{SegmentModelFiles.FileName} not found in {string.Join(", ", SegmentModelFiles.DefaultDirectories)}");

    private static void WarmUp(InferenceSession session)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(new float[3 * InputSize * InputSize], [1, 3, InputSize, InputSize]);
        using var runOptions = new RunOptions();
        using var _ = session.Run(runOptions, [session.InputMetadata.Keys.First()], [input], [.. session.OutputMetadata.Keys]);
    }

    public void Segment(VisionFrame frame, Span<float> mask)
    {
        if (mask.Length < InputSize * InputSize)
            throw new ArgumentException("The mask is too small.", nameof(mask));
        // Outside the gate: only the run itself has to wait for other models.
        _preprocessor.Run(frame.Pixels, frame.Width, frame.Height, frame.Stride, _input, InputSize, InputSize, Mean, Std);
        lock (OnnxGate.Lock)
        {
            using var outputs = _session.Run(_runOptions, _inputNames, [_inputValue], _outputNames);
            outputs[0].GetTensorDataAsSpan<float>()[..(InputSize * InputSize)].CopyTo(mask);
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

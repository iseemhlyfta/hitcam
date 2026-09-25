using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HitCam.Vision;

/// <summary>Finds objects in a frame. The engine only knows this, so tests can run it without a model.</summary>
public interface IObjectDetector : IDisposable
{
    ModelInfo Model { get; }

    /// <summary>"DirectML" or "CPU".</summary>
    string Provider { get; }

    IReadOnlyList<Detection> Detect(VisionFrame frame, DetectionOptions options);
}

public enum ProviderPreference
{
    /// <summary>DirectML (any DirectX 12 GPU) if it loads and runs the model, the CPU otherwise.</summary>
    Auto,
    Cpu,
}

/// <summary>An RF-DETR model in ONNX Runtime. Not thread-safe: one thread runs it.</summary>
public sealed class Detector : IObjectDetector
{
    public const string DirectML = "DirectML";
    public const string Cpu = "CPU";

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly Preprocessor _preprocessor = new();
    private readonly float[] _input;
    private readonly OrtValue _inputValue;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;

    private Detector(ModelInfo model, InferenceSession session, string provider, string? gpuError)
    {
        Model = model;
        _session = session;
        Provider = provider;
        GpuError = gpuError;

        var (inputName, metadata) = session.InputMetadata.First();
        if (metadata.ElementDataType != TensorElementType.Float)
            throw new ModelFormatException($"the input must be float32, not {metadata.ElementDataType}");
        if (!session.OutputMetadata.ContainsKey(model.BoxesOutput) || !session.OutputMetadata.ContainsKey(model.LogitsOutput))
            throw new ModelFormatException($"the model has no outputs \"{model.BoxesOutput}\" and \"{model.LogitsOutput}\"");

        _input = new float[3 * model.InputWidth * model.InputHeight];
        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, [1, 3, model.InputHeight, model.InputWidth]);
        _inputNames = [inputName];
        _outputNames = [model.BoxesOutput, model.LogitsOutput];
    }

    public ModelInfo Model { get; }

    public string Provider { get; }

    /// <summary>Why DirectML is not used, when it was tried and failed.</summary>
    public string? GpuError { get; }

    /// <summary>Time of the last <see cref="Detect"/>: preprocessing, inference and decoding.</summary>
    public double LastMilliseconds { get; private set; }

    /// <summary>
    /// Loads the model: DirectML first (checked with one inference), the CPU if that fails. Takes a second or two;
    /// throws <see cref="ModelFormatException"/> or <see cref="OnnxRuntimeException"/> if the model cannot run at all.
    /// </summary>
    public static Detector Load(ModelInfo model, ProviderPreference preference = ProviderPreference.Auto)
    {
        if (!File.Exists(model.ModelPath))
            throw new FileNotFoundException("The model file is missing.", model.ModelPath);

        string? gpuError = null;
        if (preference == ProviderPreference.Auto)
        {
            Detector? detector = null;
            try
            {
                var options = new SessionOptions
                {
                    // DirectML does not support memory patterns or parallel execution.
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                options.AppendExecutionProvider_DML(0);
                detector = Create(model, options, DirectML, null);
                detector.WarmUp();
                return detector;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
            {
                detector?.Dispose();
                gpuError = ex.Message;
            }
        }

        var cpuOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // Leave cores for the decoder, the network and the app.
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        var cpu = Create(model, cpuOptions, Cpu, gpuError);
        try
        {
            cpu.WarmUp();
        }
        catch
        {
            cpu.Dispose();
            throw;
        }
        return cpu;
    }

    private static Detector Create(ModelInfo model, SessionOptions options, string provider, string? gpuError)
    {
        InferenceSession? session = null;
        try
        {
            lock (OnnxGate.Lock)
                session = new InferenceSession(model.ModelPath, options);
            return new Detector(model, session, provider, gpuError);
        }
        catch
        {
            session?.Dispose();
            throw;
        }
        finally
        {
            options.Dispose();
        }
    }

    private void WarmUp()
    {
        Array.Clear(_input);
        lock (OnnxGate.Lock)
        {
            using var _ = _session.Run(_runOptions, _inputNames, [_inputValue], _outputNames);
        }
    }

    public IReadOnlyList<Detection> Detect(VisionFrame frame, DetectionOptions options) =>
        Detect(frame.Pixels, frame.Width, frame.Height, frame.Stride, options);

    public IReadOnlyList<Detection> Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride, DetectionOptions options)
    {
        var started = Stopwatch.GetTimestamp();
        _preprocessor.Run(bgra, width, height, stride, _input, Model.InputWidth, Model.InputHeight, Model.Mean, Model.Std);
        IReadOnlyList<Detection> detections;
        lock (OnnxGate.Lock)
        {
            using var outputs = _session.Run(_runOptions, _inputNames, [_inputValue], _outputNames);
            var boxes = outputs[0];
            var logits = outputs[1];
            var logitsShape = logits.GetTensorTypeAndShape().Shape;
            var columns = (int)logitsShape[^1];
            var queries = (int)logitsShape[^2];
            detections = Decoder.Decode(boxes.GetTensorDataAsSpan<float>(), logits.GetTensorDataAsSpan<float>(), queries, columns, Model.Classes, options);
        }
        LastMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return detections;
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

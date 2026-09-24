using Microsoft.ML.OnnxRuntime;

namespace HitCam.Vision.Hands;

/// <summary>Opens a small model on DirectML if it loads and runs there, on the CPU otherwise.</summary>
internal static class OnnxLoader
{
    /// <param name="warmUp">One inference, so a provider that loads but cannot run the model fails here.</param>
    public static (InferenceSession Session, string Provider) Load(
        string path, ProviderPreference preference, Action<InferenceSession> warmUp)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The model file is missing.", path);

        if (preference == ProviderPreference.Auto)
        {
            InferenceSession? session = null;
            try
            {
                using var options = new SessionOptions
                {
                    // DirectML does not support memory patterns or parallel execution.
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                options.AppendExecutionProvider_DML(0);
                session = new InferenceSession(path, options);
                warmUp(session);
                return (session, Detector.DirectML);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
            {
                session?.Dispose();
            }
        }

        using var cpuOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // Small models: two threads are plenty and leave the rest to the decoder and the app.
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 2),
        };
        var cpu = new InferenceSession(path, cpuOptions);
        try
        {
            warmUp(cpu);
            return (cpu, Detector.Cpu);
        }
        catch
        {
            cpu.Dispose();
            throw;
        }
    }
}

namespace HitCam.Vision;

/// <summary>
/// One ONNX Runtime call at a time in this process. Object analysis and hand tracking run their models on their own
/// threads; with the DirectML provider, sessions running at the same moment corrupted memory inside onnxruntime.dll
/// (access violations after 10–20 minutes with both on, 2026-09-26). Creating sessions, running them and releasing
/// their outputs all take this lock; preparing inputs and reading results happen outside where possible.
/// </summary>
public static class OnnxGate
{
    public static readonly Lock Lock = new();
}

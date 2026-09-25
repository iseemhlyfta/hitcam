using System.Collections.Concurrent;
using HitCam.Core.Server;
using HitCam.Vision;

namespace HitCam.Desktop.Services;

/// <summary>
/// Decodes the phone's H.264 on a dedicated thread and publishes frames to the virtual camera.
/// After a drop or a decoder error it skips to the next keyframe and asks the phone for one.
/// </summary>
public sealed class VideoPipeline : IDisposable
{
    // Null is the "no signal" marker.
    private readonly BlockingCollection<VideoFrame?> _queue = new(boundedCapacity: 16);
    private readonly Thread _thread;
    // Guards the handle against destruction while the UI thread copies the preview.
    private readonly Lock _bridgeLock = new();
    private IntPtr _bridge;
    private HitCamProcessing _processing;
    private int _dropped;
    private long _decodedFrames;
    private volatile bool _isLinked;
    private volatile string? _error;
    private volatile bool _overlayUnsupported;
    private volatile bool _shotUnsupported;
    private readonly bool _previewOnly;

    /// <param name="previewOnly">
    /// Frames go to the preview only, never to the "HitCam" camera (a debug instance started with --port must not
    /// feed the camera another instance serves).
    /// </param>
    public VideoPipeline(bool previewOnly = false)
    {
        _previewOnly = previewOnly;
        _thread = new Thread(Run) { IsBackground = true, Name = "HitCam decoder" };
        _thread.Start();
    }

    /// <summary>Raised on the decoder thread when decoding can only resume from a keyframe.</summary>
    public event Action? KeyframeNeeded;

    /// <summary>True once decoded frames can reach the camera service.</summary>
    public bool IsLinked => _isLinked;

    public long DecodedFrames => Interlocked.Read(ref _decodedFrames);

    /// <summary>Set if the decoder could not be created.</summary>
    public string? Error => _error;

    public void Push(VideoFrame frame)
    {
        if (!TryAdd(frame))
            Volatile.Write(ref _dropped, 1);
    }

    public void ClearSignal() => TryAdd(null);

    /// <summary>
    /// PC-side picture processing (noise reduction, colour, sharpness). Cheap: call on every slider change. Kept across
    /// decoder restarts; applied once the decoder exists.
    /// </summary>
    public void SetProcessing(in HitCamProcessing settings)
    {
        lock (_bridgeLock)
        {
            _processing = settings;
            if (_bridge != IntPtr.Zero)
                NativeMethods.HitCam_BridgeSetProcessing(_bridge, in _processing);
        }
    }

    /// <summary>Last frame's processing times and the artifact reduction error (see <see cref="Services.ProcessingStats"/>).</summary>
    public ProcessingStats ProcessingStats()
    {
        lock (_bridgeLock)
        {
            if (_bridge == IntPtr.Zero)
                return new ProcessingStats(null, null, 0);
            NativeMethods.HitCam_BridgeProcessingStats(_bridge, out var gpu, out var artifact, out var error);
            return new ProcessingStats(gpu >= 0 ? gpu : null, artifact >= 0 ? artifact : null, error);
        }
    }

    /// <summary>
    /// Whether NVIDIA RTX compression artifact reduction can run on this PC. May load large NVIDIA libraries: call off
    /// the UI thread.
    /// </summary>
    public static bool IsArtifactReductionAvailable()
    {
        try
        {
            return NativeMethods.HitCam_ArtifactReductionAvailable();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>Size and sequence number of the newest decoded frame's preview; (0, 0, 0) before the first.</summary>
    public (int Width, int Height, ulong Frame) PreviewInfo()
    {
        lock (_bridgeLock)
        {
            if (_bridge == IntPtr.Zero)
                return (0, 0, 0);
            NativeMethods.HitCam_BridgePreviewInfo(_bridge, out var width, out var height, out var frame);
            return ((int)width, (int)height, frame);
        }
    }

    /// <summary>Copies the preview as BGRA into <paramref name="destination"/>; false if its size changed meanwhile.</summary>
    public bool CopyPreview(IntPtr destination, int stride, int width, int height)
    {
        lock (_bridgeLock)
        {
            return _bridge != IntPtr.Zero
                   && NativeMethods.HitCam_BridgeCopyPreview(_bridge, destination, (uint)stride, (uint)width, (uint)height);
        }
    }

    /// <summary>
    /// For object analysis (any thread): copies the newest preview into <paramref name="target"/> if it is newer than
    /// <paramref name="previousFrame"/>. Only called when the analysis is ready for a frame, so frames it has no time
    /// for are never copied.
    /// </summary>
    public unsafe bool CopyPreviewTo(ulong previousFrame, VisionFrame target)
    {
        lock (_bridgeLock)
        {
            if (_bridge == IntPtr.Zero)
                return false;
            NativeMethods.HitCam_BridgePreviewInfo(_bridge, out var width, out var height, out var frame);
            if (width == 0 || height == 0 || frame == previousFrame)
                return false;
            target.SetSize((int)width, (int)height);
            fixed (byte* pixels = target.Buffer)
            {
                if (!NativeMethods.HitCam_BridgeCopyPreview(_bridge, (IntPtr)pixels, (uint)target.Stride, width, height))
                    return false;
            }
            target.Sequence = frame;
            return true;
        }
    }

    /// <summary>
    /// Boxes drawn into the camera picture (see <see cref="CameraOverlay"/>); empty clears them. The DLL copies them
    /// during the call. Does nothing before the decoder exists or with a DLL that predates overlays.
    /// </summary>
    public unsafe void SetOverlay(ReadOnlySpan<HitCamOverlayBox> boxes)
    {
        if (_overlayUnsupported)
            return;
        lock (_bridgeLock)
        {
            if (_bridge == IntPtr.Zero)
                return;
            try
            {
                fixed (HitCamOverlayBox* pointer = boxes)
                    NativeMethods.HitCam_BridgeSetOverlay(_bridge, pointer, boxes.Length);
            }
            catch (EntryPointNotFoundException)
            {
                _overlayUnsupported = true;
            }
        }
    }

    /// <summary>
    /// A finger-gun shot played in the camera picture (never the preview). Any thread. Does nothing before the
    /// decoder exists or with a DLL that predates shots.
    /// </summary>
    public void Shot(HitCamShot shot)
    {
        if (_shotUnsupported)
            return;
        lock (_bridgeLock)
        {
            if (_bridge == IntPtr.Zero)
                return;
            try
            {
                NativeMethods.HitCam_BridgeShot(_bridge, shot);
            }
            catch (EntryPointNotFoundException)
            {
                _shotUnsupported = true;
            }
        }
    }

    private bool TryAdd(VideoFrame? item)
    {
        try
        {
            return _queue.TryAdd(item);
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
            return true;
        }
    }

    private void Run()
    {
        IntPtr bridge;
        try
        {
            var hr = NativeMethods.HitCam_BridgeCreate(out bridge);
            if (hr < 0)
            {
                _error = $"0x{hr:X8}";
                Discard();
                return;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _error = NativeDiagnostics.ExplainLoadFailure(ex);
            Discard();
            return;
        }

        if (_previewOnly)
            NativeMethods.HitCam_BridgePreviewOnly(bridge);
        lock (_bridgeLock)
        {
            _bridge = bridge;
            NativeMethods.HitCam_BridgeSetProcessing(bridge, in _processing);
        }
        try
        {
            var waitForKeyframe = true;
            foreach (var frame in _queue.GetConsumingEnumerable())
            {
                if (frame is null)
                {
                    NativeMethods.HitCam_BridgeClearSignal(bridge);
                    waitForKeyframe = true;
                    continue;
                }

                if (Interlocked.Exchange(ref _dropped, 0) == 1 && !frame.Keyframe)
                {
                    waitForKeyframe = true;
                    KeyframeNeeded?.Invoke();
                }
                if (waitForKeyframe && !frame.Keyframe)
                    continue;

                var result = NativeMethods.HitCam_BridgeDecode(bridge, frame.Data, (uint)frame.Data.Length, (long)frame.PhoneTimestamp * 10);
                if (result < 0)
                {
                    waitForKeyframe = true;
                    KeyframeNeeded?.Invoke();
                    continue;
                }

                waitForKeyframe = false;
                Interlocked.Increment(ref _decodedFrames);
                _isLinked = NativeMethods.HitCam_BridgeIsLinked(bridge);
            }
        }
        finally
        {
            // Unpublish under the lock, destroy outside it: destroying waits for a model that may still be loading
            // (seconds), and the UI thread must not block on the lock meanwhile. Nobody can reach the handle now.
            lock (_bridgeLock)
                _bridge = IntPtr.Zero;
            NativeMethods.HitCam_BridgeDestroy(bridge);
        }
    }

    // Without a decoder, keep the queue empty so producers never block.
    private void Discard()
    {
        foreach (var _ in _queue.GetConsumingEnumerable())
        {
        }
    }

    public void Dispose()
    {
        // The queue itself is not disposed: a late Push from a network thread must not throw.
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}

using System.Collections.Concurrent;
using HitCam.Core.Server;

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
    private DenoiseMode _denoise;
    private int _dropped;
    private long _decodedFrames;
    private volatile bool _isLinked;
    private volatile string? _error;

    public VideoPipeline()
    {
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

    /// <summary>NVIDIA AI noise removal mode. Kept across decoder restarts; applied once the decoder exists.</summary>
    public void SetDenoise(DenoiseMode mode)
    {
        lock (_bridgeLock)
        {
            _denoise = mode;
            if (_bridge != IntPtr.Zero)
                NativeMethods.HitCam_BridgeSetDenoiseMode(_bridge, (int)mode);
        }
    }

    /// <summary>
    /// Last denoised frame's time (ms, null if none yet), the NVIDIA status of a failed model load (0 if none),
    /// the measured noise level (null before the first frame) and the amount applied to the last frame (0..1).
    /// </summary>
    public DenoiseStats DenoiseStats()
    {
        lock (_bridgeLock)
        {
            if (_bridge == IntPtr.Zero)
                return new DenoiseStats(null, 0, null, 0);
            NativeMethods.HitCam_BridgeDenoiseStats(_bridge, out var milliseconds, out var error, out var noise, out var amount);
            return new DenoiseStats(milliseconds >= 0 ? milliseconds : null, error, noise >= 0 ? noise : null, amount);
        }
    }

    /// <summary>Whether the NVIDIA Video Effects runtime is installed. Loads large NVIDIA libraries: call off the UI thread.</summary>
    public static bool IsDenoiseAvailable()
    {
        try
        {
            return NativeMethods.HitCam_DenoiseAvailable();
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
            _error = ex.Message;
            Discard();
            return;
        }

        lock (_bridgeLock)
        {
            _bridge = bridge;
            NativeMethods.HitCam_BridgeSetDenoiseMode(bridge, (int)_denoise);
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

/// <summary>AI noise removal state of the last frame (see <see cref="VideoPipeline.DenoiseStats"/>).</summary>
public readonly record struct DenoiseStats(double? Milliseconds, int Error, double? Noise, float Amount);

/// <summary>AI noise removal modes; the numbers match HitCam_BridgeSetDenoiseMode.</summary>
public enum DenoiseMode
{
    Off = 0,
    /// <summary>Gentle model only as far as noise is measured; a clean picture is left untouched.</summary>
    Fast = 1,
    /// <summary>Gentle model on every frame.</summary>
    General = 2,
    /// <summary>Strong model on every frame.</summary>
    Maximum = 3,
}

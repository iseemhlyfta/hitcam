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

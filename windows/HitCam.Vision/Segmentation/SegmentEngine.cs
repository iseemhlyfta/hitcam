using System.Diagnostics;
using System.Drawing;

namespace HitCam.Vision.Segmentation;

/// <summary>
/// One analysed frame: the person mask (<see cref="Width"/>×<see cref="Height"/> bytes, 255 = person, the whole frame
/// stretched), the box around the person (normalized; null if nobody), and the preview frame it was taken from.
/// </summary>
public sealed record SegmentResult(byte[] Mask, int Width, int Height, RectangleF? Person, VisionStats Stats, ulong Sequence);

/// <summary>
/// Runs person segmentation on its own thread, like <see cref="Faces.FaceEngine"/>, but at most
/// <see cref="Interval"/> apart: the mask moves slowly next to faces and hands, which share ONNX Runtime with it
/// (<see cref="OnnxGate"/>). When a run takes long (the gate is busy, a slow processor), the interval grows. Results
/// and status changes are raised on the engine thread.
/// </summary>
public sealed class SegmentEngine : IDisposable
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(10);

    /// <summary>The intervals tried in turn as runs get slower: 15, 10 and 7.5 masks a second.</summary>
    public static readonly TimeSpan[] Intervals = [TimeSpan.FromMilliseconds(66), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(133)];

    private readonly FrameSource _source;
    private readonly Func<ISegmentModel> _modelFactory;
    private readonly Func<ISegmentModel> _fallbackFactory;
    private readonly TimeProvider _time;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _lock = new();
    private int _requestVersion;
    private bool _requested;
    private VisionStatus _status = VisionStatus.Stopped;
    private int _reset;
    private volatile bool _paused;
    private volatile bool _disposed;

    /// <param name="modelFactory">Loads the model; <see cref="SelfieSegmenter.LoadDefault"/> by default. May throw.</param>
    /// <param name="fallbackFactory">Loads it again when it fails while running: the processor by default.</param>
    public SegmentEngine(FrameSource source, Func<ISegmentModel>? modelFactory = null, TimeProvider? time = null,
        Func<ISegmentModel>? fallbackFactory = null)
    {
        _source = source;
        _modelFactory = modelFactory ?? (() => SelfieSegmenter.LoadDefault());
        _fallbackFactory = fallbackFactory ?? (modelFactory is null ? () => SelfieSegmenter.LoadDefault(ProviderPreference.Cpu) : _modelFactory);
        _time = time ?? TimeProvider.System;
        _thread = new Thread(Run) { IsBackground = true, Name = "HitCam segmentation" };
        _thread.Start();
    }

    public event Action<SegmentResult>? ResultReady;

    public event Action<VisionStatus>? StatusChanged;

    public VisionStatus Status
    {
        get
        {
            lock (_lock)
                return _status;
        }
    }

    /// <summary>The current time between masks (grows while runs are slow).</summary>
    public TimeSpan Interval { get; private set; } = Intervals[0];

    /// <summary>No frames are coming: wait without polling; the model stays loaded.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            _wake.Set();
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_requested && _status.State != VisionState.Failed)
                return;
            _requested = true;
            _requestVersion++;
        }
        _wake.Set();
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_requested)
                return;
            _requested = false;
            _requestVersion++;
        }
        _wake.Set();
    }

    /// <summary>Forgets the mask history before the next frame (a new stream).</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _reset, 1);
        _wake.Set();
    }

    /// <summary>The interval for runs that took <paramref name="averageMilliseconds"/> on average.</summary>
    internal static TimeSpan IntervalFor(double averageMilliseconds) =>
        averageMilliseconds > 40 ? Intervals[2] : averageMilliseconds > 25 ? Intervals[1] : Intervals[0];

    private void Run()
    {
        ISegmentModel? model = null;
        MaskFilter? filter = null;
        float[] raw = [];
        var loadedVersion = 0;
        var onFallback = false;
        var frame = new VisionFrame();
        ulong lastSequence = 0;
        var lastRun = long.MinValue;
        var averageMilliseconds = 0.0;
        var fps = new RateMeter();
        try
        {
            while (!_disposed)
            {
                int version;
                bool requested;
                lock (_lock)
                {
                    version = _requestVersion;
                    requested = _requested;
                }

                if (version != loadedVersion)
                {
                    loadedVersion = version;
                    onFallback = false;
                    model?.Dispose();
                    model = null;
                    fps.Reset();
                    lastSequence = 0;
                    if (requested)
                    {
                        SetStatus(version, new VisionStatus(VisionState.Loading, null, null, null));
                        try
                        {
                            model = _modelFactory();
                            SetStatus(version, new VisionStatus(VisionState.Running, null, model.Provider, null));
                        }
                        catch (Exception ex)
                        {
                            SetStatus(version, new VisionStatus(VisionState.Failed, null, null, ex.Message));
                        }
                    }
                    else
                    {
                        SetStatus(version, VisionStatus.Stopped);
                    }
                    continue;
                }

                if (model is null)
                {
                    _wake.WaitOne();
                    continue;
                }
                if (filter is null || filter.Width != model.Size)
                {
                    filter = new MaskFilter(model.Size, model.Size);
                    raw = new float[model.Size * model.Size];
                }
                if (Interlocked.Exchange(ref _reset, 0) == 1)
                    filter.Reset();

                // Not more often than the interval: the rest of the time belongs to faces and hands.
                if (lastRun != long.MinValue && _time.GetElapsedTime(lastRun) is var since && since < Interval)
                {
                    _wake.WaitOne(Interval - since);
                    continue;
                }

                bool gotFrame;
                try
                {
                    gotFrame = _source(lastSequence, frame);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"HitCam segmentation: the frame source failed: {ex.Message}");
                    gotFrame = false;
                }
                if (!gotFrame)
                {
                    _wake.WaitOne(_paused ? Timeout.InfiniteTimeSpan : IdlePoll);
                    continue;
                }
                lastSequence = frame.Sequence;

                var started = _time.GetTimestamp();
                lastRun = started;
                try
                {
                    model.Segment(frame, raw);
                }
                catch (Exception ex)
                {
                    model.Dispose();
                    model = null;
                    if (!onFallback)
                    {
                        onFallback = true;
                        Trace.TraceWarning($"HitCam segmentation: the model failed ({ex.Message}); loading it again on the fallback");
                        SetStatus(version, new VisionStatus(VisionState.Loading, null, null, null));
                        try
                        {
                            model = _fallbackFactory();
                            SetStatus(version, new VisionStatus(VisionState.Running, null, model.Provider, null));
                            continue;
                        }
                        catch (Exception fallbackError)
                        {
                            ex = fallbackError;
                        }
                    }
                    SetStatus(version, new VisionStatus(VisionState.Failed, null, null, ex.Message));
                    continue;
                }
                var (mask, person) = filter.Apply(raw);
                var now = _time.GetTimestamp();
                var milliseconds = _time.GetElapsedTime(started, now).TotalMilliseconds;
                averageMilliseconds = averageMilliseconds == 0 ? milliseconds : averageMilliseconds * 0.8 + milliseconds * 0.2;
                Interval = IntervalFor(averageMilliseconds);

                lock (_lock)
                {
                    if (_requestVersion != version)
                        continue;
                }
                var rate = fps.Add(_time.GetElapsedTime(0, now));
                try
                {
                    ResultReady?.Invoke(new SegmentResult(mask, filter.Width, filter.Height, person,
                        new VisionStats(milliseconds, rate, model.Provider), frame.Sequence));
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"HitCam segmentation: a result handler failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"HitCam segmentation: {ex}");
        }
        finally
        {
            model?.Dispose();
        }
    }

    private void SetStatus(int version, VisionStatus status)
    {
        lock (_lock)
        {
            if (_requestVersion != version)
                return;
            _status = status;
        }
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"HitCam segmentation: a status handler failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _wake.Set();
        if (_thread.Join(TimeSpan.FromSeconds(5)))
            _wake.Dispose();
    }
}

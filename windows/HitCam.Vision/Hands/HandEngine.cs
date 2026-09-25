using System.Diagnostics;

namespace HitCam.Vision.Hands;

/// <summary>
/// One analysed frame: the hands to show (points normalized to the frame, poses), the shots fired in it and the
/// threads between fingertips with the fills between them.
/// </summary>
public sealed record HandResult(
    IReadOnlyList<TrackedHand> Hands, VisionStats Stats, ulong Sequence, int FrameWidth, int FrameHeight, IReadOnlyList<Shot> Shots,
    IReadOnlyList<FingerThread> Threads, IReadOnlyList<ThreadFill> Fills)
{
    public HandResult(IReadOnlyList<TrackedHand> hands, VisionStats stats, ulong sequence, int frameWidth, int frameHeight)
        : this(hands, stats, sequence, frameWidth, frameHeight, [], [], []) { }

    public HandResult(IReadOnlyList<TrackedHand> hands, VisionStats stats, ulong sequence, int frameWidth, int frameHeight, IReadOnlyList<Shot> shots)
        : this(hands, stats, sequence, frameWidth, frameHeight, shots, [], []) { }
}

/// <summary>
/// Runs hand tracking on its own thread, like <see cref="VisionEngine"/>: frames are pulled only when the previous
/// one is done, so the video never waits. Start and stop from any thread; results and status changes are raised on
/// the engine thread. <see cref="VisionStatus.Model"/> is always null here.
/// </summary>
public sealed class HandEngine : IDisposable
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(10);

    private readonly FrameSource _source;
    private readonly Func<IHandModels> _modelFactory;
    private readonly HandTrackerOptions? _trackerOptions;
    private readonly TimeProvider _time;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _lock = new();
    private int _requestVersion;
    private bool _requested;
    private VisionStatus _status = VisionStatus.Stopped;
    private int _resetTracks;
    private volatile bool _disposed;
    private long _analysedFrames;

    /// <param name="modelFactory">Loads the models; <see cref="HandModels.LoadDefault"/> by default. May throw.</param>
    public HandEngine(FrameSource source, Func<IHandModels>? modelFactory = null, TimeProvider? time = null,
        HandTrackerOptions? trackerOptions = null)
    {
        _source = source;
        _modelFactory = modelFactory ?? HandModels.LoadDefault;
        _trackerOptions = trackerOptions;
        _time = time ?? TimeProvider.System;
        _thread = new Thread(Run) { IsBackground = true, Name = "HitCam hands" };
        _thread.Start();
    }

    public event Action<HandResult>? ResultReady;

    public event Action<VisionStatus>? StatusChanged;

    public VisionStatus Status
    {
        get
        {
            lock (_lock)
                return _status;
        }
    }

    public long AnalysedFrames => Interlocked.Read(ref _analysedFrames);

    /// <summary>Loads the models and starts tracking; does nothing if already running, retries after a failure.</summary>
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

    /// <summary>Stops tracking and unloads the models.</summary>
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

    /// <summary>Forgets all hands before the next frame (camera switched, disconnected).</summary>
    public void ResetTracks()
    {
        Interlocked.Exchange(ref _resetTracks, 1);
        _wake.Set();
    }

    private void Run()
    {
        IHandModels? models = null;
        HandTracker? tracker = null;
        var gestures = new GestureDetector();
        var threads = new FingerThreads();
        var loadedVersion = 0;
        var frame = new VisionFrame();
        ulong lastSequence = 0;
        var lastFrameSize = (0, 0);
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
                    models?.Dispose();
                    models = null;
                    tracker = null;
                    fps.Reset();
                    lastSequence = 0;
                    if (requested)
                    {
                        SetStatus(version, new VisionStatus(VisionState.Loading, null, null, null));
                        try
                        {
                            models = _modelFactory();
                            tracker = new HandTracker(models, _trackerOptions);
                            SetStatus(version, new VisionStatus(VisionState.Running, null, models.Provider, null));
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

                if (models is null || tracker is null)
                {
                    _wake.WaitOne();
                    continue;
                }

                if (Interlocked.Exchange(ref _resetTracks, 0) == 1)
                {
                    tracker.Reset();
                    gestures.Reset();
                    threads.Reset();
                    fps.Reset();
                }

                bool gotFrame;
                try
                {
                    gotFrame = _source(lastSequence, frame);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"HitCam hands: the frame source failed: {ex.Message}");
                    gotFrame = false;
                }
                if (!gotFrame)
                {
                    _wake.WaitOne(IdlePoll);
                    continue;
                }
                lastSequence = frame.Sequence;
                if ((frame.Width, frame.Height) != lastFrameSize)
                {
                    lastFrameSize = (frame.Width, frame.Height);
                    tracker.Reset();
                    gestures.Reset();
                    threads.Reset();
                }

                var started = _time.GetTimestamp();
                IReadOnlyList<TrackedHand> hands;
                IReadOnlyList<Shot> shots;
                IReadOnlyList<FingerThread> tied;
                IReadOnlyList<ThreadFill> fills;
                try
                {
                    var time = _time.GetElapsedTime(0, started);
                    (hands, shots) = gestures.Update(tracker.Update(frame, time), frame.Width, frame.Height, time);
                    (tied, fills) = threads.Update(hands, frame.Width, frame.Height);
                }
                catch (Exception ex)
                {
                    // E.g. the GPU was removed (driver update); stop instead of failing on every frame.
                    models.Dispose();
                    models = null;
                    tracker = null;
                    SetStatus(version, new VisionStatus(VisionState.Failed, null, null, ex.Message));
                    continue;
                }
                var now = _time.GetTimestamp();
                var milliseconds = _time.GetElapsedTime(started, now).TotalMilliseconds;
                Interlocked.Increment(ref _analysedFrames);

                lock (_lock)
                {
                    if (_requestVersion != version)
                        continue;
                }
                var rate = fps.Add(_time.GetElapsedTime(0, now));
                try
                {
                    ResultReady?.Invoke(new HandResult(
                        hands, new VisionStats(milliseconds, rate, models.Provider), frame.Sequence, frame.Width, frame.Height, shots, tied, fills));
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"HitCam hands: a result handler failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"HitCam hands: {ex}");
        }
        finally
        {
            models?.Dispose();
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
            Trace.TraceWarning($"HitCam hands: a status handler failed: {ex.Message}");
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

using System.Diagnostics;

namespace HitCam.Vision;

/// <summary>
/// Fills <paramref name="target"/> with the newest frame if it is newer than <paramref name="previousSequence"/>
/// (and sets its <see cref="VisionFrame.Sequence"/>); false if there is none. Called on the engine thread, only when
/// the engine is ready to analyse a frame.
/// </summary>
public delegate bool FrameSource(ulong previousSequence, VisionFrame target);

public enum VisionState
{
    /// <summary>No model loaded; nothing analysed.</summary>
    Stopped,
    Loading,
    Running,
    /// <summary>The model could not be loaded; see <see cref="VisionStatus.Error"/>.</summary>
    Failed,
}

/// <param name="Provider">"DirectML" or "CPU" once running.</param>
public sealed record VisionStatus(VisionState State, ModelInfo? Model, string? Provider, string? Error)
{
    public static VisionStatus Stopped { get; } = new(VisionState.Stopped, null, null, null);
}

/// <param name="InferenceMilliseconds">The last frame: preprocessing, inference and decoding.</param>
/// <param name="AnalysisFps">Frames analysed per second, averaged over about a second.</param>
public sealed record VisionStats(double InferenceMilliseconds, double AnalysisFps, string Provider);

/// <summary>One analysed frame: the tracks to show, normalized to the frame.</summary>
public sealed record VisionResult(
    IReadOnlyList<Track> Tracks, VisionStats Stats, ModelInfo Model, ulong Sequence, int FrameWidth, int FrameHeight);

/// <summary>
/// Runs detection and tracking on its own thread. Frames are pulled from a <see cref="FrameSource"/> only when the
/// previous one is done, so there is never a queue: while analysis is slower than the video, the frames in between
/// are simply never looked at. Start, stop and switch models from any thread; results and status changes are raised
/// on the engine thread.
/// </summary>
public sealed class VisionEngine : IDisposable
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(10);

    private readonly FrameSource _source;
    private readonly Func<ModelInfo, IObjectDetector> _detectorFactory;
    private readonly TimeProvider _time;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Tracker _tracker;
    private readonly Lock _lock = new();
    private Request _request = new(null, 0);
    private DetectionOptions _options = DetectionOptions.Default;
    private VisionStatus _status = VisionStatus.Stopped;
    private int _resetTracks;
    private volatile bool _disposed;
    private long _analysedFrames;

    /// <param name="detectorFactory">Loads a model; <see cref="Detector.Load"/> by default. May throw.</param>
    /// <param name="time">Clock for tracking and the frame rate (fake time in tests).</param>
    public VisionEngine(
        FrameSource source, Func<ModelInfo, IObjectDetector>? detectorFactory = null, TimeProvider? time = null,
        TrackerOptions? trackerOptions = null)
    {
        _source = source;
        _detectorFactory = detectorFactory ?? (model => Detector.Load(model));
        _time = time ?? TimeProvider.System;
        _tracker = new Tracker(trackerOptions);
        _thread = new Thread(Run) { IsBackground = true, Name = "HitCam vision" };
        _thread.Start();
    }

    /// <summary>Raised on the engine thread for every analysed frame.</summary>
    public event Action<VisionResult>? ResultReady;

    /// <summary>Raised on the engine thread when the model starts loading, runs, fails or is unloaded.</summary>
    public event Action<VisionStatus>? StatusChanged;

    public VisionStatus Status
    {
        get
        {
            lock (_lock)
                return _status;
        }
    }

    /// <summary>Threshold and class filter; applies from the next frame.</summary>
    public DetectionOptions Options
    {
        get => Volatile.Read(ref _options);
        set => Volatile.Write(ref _options, value ?? DetectionOptions.Default);
    }

    /// <summary>Frames analysed since the engine was created.</summary>
    public long AnalysedFrames => Interlocked.Read(ref _analysedFrames);

    /// <summary>
    /// Loads <paramref name="model"/> (unloading any other) and starts analysing. Calling it again with the model
    /// already running does nothing; with a model that failed, it tries again.
    /// </summary>
    public void Start(ModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_lock)
        {
            if (_request.Model is { } current && SameModel(current, model) && _status.State != VisionState.Failed)
                return;
            _request = new Request(model, _request.Version + 1);
        }
        _wake.Set();
    }

    /// <summary>
    /// Stops analysing and unloads the model (frees its GPU memory). A result being published right now may still
    /// arrive: handlers should check their own state.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_request.Model is null)
                return;
            _request = new Request(null, _request.Version + 1);
        }
        _wake.Set();
    }

    /// <summary>Forgets all tracks before the next frame (camera switched, disconnected).</summary>
    public void ResetTracks()
    {
        Interlocked.Exchange(ref _resetTracks, 1);
        _wake.Set();
    }

    /// <summary>Optional hint that a new frame exists, so an idle engine does not wait for its next poll.</summary>
    public void FrameAvailable() => _wake.Set();

    private void Run()
    {
        IObjectDetector? detector = null;
        var loadedVersion = 0;
        var frame = new VisionFrame();
        ulong lastSequence = 0;
        var lastFrameSize = (0, 0);
        var fps = new RateMeter();
        try
        {
            while (!_disposed)
            {
                Request request;
                lock (_lock)
                    request = _request;

                if (request.Version != loadedVersion)
                {
                    loadedVersion = request.Version;
                    detector?.Dispose();
                    detector = null;
                    _tracker.Reset();
                    fps.Reset();
                    lastSequence = 0;
                    if (request.Model is { } model)
                    {
                        SetStatus(request, new VisionStatus(VisionState.Loading, model, null, null));
                        try
                        {
                            detector = _detectorFactory(model);
                            SetStatus(request, new VisionStatus(VisionState.Running, model, detector.Provider, null));
                        }
                        catch (Exception ex)
                        {
                            SetStatus(request, new VisionStatus(VisionState.Failed, model, null, ex.Message));
                        }
                    }
                    else
                    {
                        SetStatus(request, VisionStatus.Stopped);
                    }
                    continue;
                }

                if (detector is null)
                {
                    _wake.WaitOne();
                    continue;
                }

                if (Interlocked.Exchange(ref _resetTracks, 0) == 1)
                {
                    _tracker.Reset();
                    fps.Reset();
                }

                bool gotFrame;
                try
                {
                    gotFrame = _source(lastSequence, frame);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"HitCam vision: the frame source failed: {ex.Message}");
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
                    // Another camera or orientation: old boxes mean nothing in the new picture.
                    lastFrameSize = (frame.Width, frame.Height);
                    _tracker.Reset();
                }

                var started = _time.GetTimestamp();
                IReadOnlyList<Detection> detections;
                try
                {
                    detections = detector.Detect(frame, Options);
                }
                catch (Exception ex)
                {
                    // E.g. the GPU was removed (driver update); stop instead of failing on every frame.
                    detector.Dispose();
                    detector = null;
                    SetStatus(request, new VisionStatus(VisionState.Failed, request.Model, null, ex.Message));
                    continue;
                }
                var now = _time.GetTimestamp();
                var milliseconds = _time.GetElapsedTime(started, now).TotalMilliseconds;
                var tracks = _tracker.Update(detections, _time.GetElapsedTime(0, now));
                Interlocked.Increment(ref _analysedFrames);

                bool current;
                lock (_lock)
                    current = _request.Version == request.Version;
                if (!current)
                    continue;
                var rate = fps.Add(_time.GetElapsedTime(0, now));
                try
                {
                    ResultReady?.Invoke(new VisionResult(
                        tracks, new VisionStats(milliseconds, rate, detector.Provider), request.Model!, frame.Sequence, frame.Width, frame.Height));
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"HitCam vision: a result handler failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"HitCam vision: {ex}");
        }
        finally
        {
            detector?.Dispose();
        }
    }

    private void SetStatus(Request request, VisionStatus status)
    {
        lock (_lock)
        {
            // A newer request overtook this one: its own status follows.
            if (_request.Version != request.Version)
                return;
            _status = status;
        }
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"HitCam vision: a status handler failed: {ex.Message}");
        }
    }

    private static bool SameModel(ModelInfo a, ModelInfo b) =>
        ReferenceEquals(a, b) || (a.Id == b.Id && string.Equals(a.ModelPath, b.ModelPath, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _wake.Set();
        // A model may be loading (a second or two); the thread is a background one either way.
        if (_thread.Join(TimeSpan.FromSeconds(5)))
            _wake.Dispose();
    }

    private sealed record Request(ModelInfo? Model, int Version);
}

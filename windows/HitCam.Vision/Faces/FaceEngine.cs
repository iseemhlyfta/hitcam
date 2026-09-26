using System.Collections.Concurrent;
using System.Diagnostics;

namespace HitCam.Vision.Faces;

/// <summary>One analysed frame: the faces (squares normalized to the frame, hidden or uncovered).</summary>
public sealed record FaceResult(IReadOnlyList<TrackedFace> Faces, VisionStats Stats, ulong Sequence, int FrameWidth, int FrameHeight);

/// <summary>
/// Runs face tracking on its own thread, like <see cref="Hands.HandEngine"/>: frames are pulled only when the previous
/// one is done. Clicks come in through <see cref="Toggle"/> and are applied before the next frame. The uncovered
/// people outlive stopping and starting (models unloaded), until the app closes. Results and status changes are raised
/// on the engine thread.
/// </summary>
public sealed class FaceEngine : IDisposable
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(10);

    private readonly FrameSource _source;
    private readonly Func<IFaceModels> _modelFactory;
    private readonly FaceTrackerOptions? _trackerOptions;
    private readonly TimeProvider _time;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _lock = new();
    private readonly FacePeople _people = new();
    private readonly ConcurrentQueue<Action<FaceTracker>> _commands = new();
    private int _requestVersion;
    private bool _requested;
    private VisionStatus _status = VisionStatus.Stopped;
    private int _resetTracks;
    private volatile bool _disposed;

    /// <param name="modelFactory">Loads the models; <see cref="FaceModels.LoadDefault"/> by default. May throw.</param>
    public FaceEngine(FrameSource source, Func<IFaceModels>? modelFactory = null, TimeProvider? time = null,
        FaceTrackerOptions? trackerOptions = null)
    {
        _source = source;
        _modelFactory = modelFactory ?? FaceModels.LoadDefault;
        _trackerOptions = trackerOptions;
        _time = time ?? TimeProvider.System;
        _thread = new Thread(Run) { IsBackground = true, Name = "HitCam faces" };
        _thread.Start();
    }

    public event Action<FaceResult>? ResultReady;

    public event Action<VisionStatus>? StatusChanged;

    public VisionStatus Status
    {
        get
        {
            lock (_lock)
                return _status;
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

    /// <summary>Forgets the faces in the picture before the next frame; the uncovered people are kept.</summary>
    public void ResetTracks()
    {
        Interlocked.Exchange(ref _resetTracks, 1);
        _wake.Set();
    }

    /// <summary>Uncovers a hidden face or hides an uncovered one (any thread; applied before the next frame).</summary>
    public void Toggle(int faceId)
    {
        _commands.Enqueue(t => t.Toggle(faceId));
        _wake.Set();
    }

    /// <summary>Hides everyone again: forgets all uncovered people.</summary>
    public void ForgetPeople()
    {
        _commands.Enqueue(t => t.ForgetPeople());
        _wake.Set();
    }

    private void Run()
    {
        IFaceModels? models = null;
        FaceTracker? tracker = null;
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
                            tracker = new FaceTracker(models, _people, _trackerOptions);
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
                    fps.Reset();
                }
                while (_commands.TryDequeue(out var command))
                    command(tracker);

                bool gotFrame;
                try
                {
                    gotFrame = _source(lastSequence, frame);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"HitCam faces: the frame source failed: {ex.Message}");
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
                }

                var started = _time.GetTimestamp();
                IReadOnlyList<TrackedFace> faces;
                try
                {
                    faces = tracker.Update(frame, _time.GetElapsedTime(0, started));
                }
                catch (Exception ex)
                {
                    models.Dispose();
                    models = null;
                    tracker = null;
                    SetStatus(version, new VisionStatus(VisionState.Failed, null, null, ex.Message));
                    continue;
                }
                var now = _time.GetTimestamp();
                var milliseconds = _time.GetElapsedTime(started, now).TotalMilliseconds;

                lock (_lock)
                {
                    if (_requestVersion != version)
                        continue;
                }
                var rate = fps.Add(_time.GetElapsedTime(0, now));
                try
                {
                    ResultReady?.Invoke(new FaceResult(faces, new VisionStats(milliseconds, rate, models.Provider), frame.Sequence, frame.Width, frame.Height));
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"HitCam faces: a result handler failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"HitCam faces: {ex}");
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
            Trace.TraceWarning($"HitCam faces: a status handler failed: {ex.Message}");
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

using System.Diagnostics;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using HitCam.Core.Protocol;
using HitCam.Desktop.Services;
using ReactiveUI;

namespace HitCam.Desktop.ViewModels;

/// <param name="Name">The phone's own name for the lens.</param>
/// <param name="Label">Short label for the lens switch.</param>
public sealed record CameraOption(string Id, string Name, string Label)
{
    public override string ToString() => Label;

    // Known phone lenses, ordered by field of view; unknown ones keep the phone's name and go last.
    private static readonly string[] Order = ["back-ultrawide", "back-wide", "back-tele", "front"];

    public static IEnumerable<CameraOption> FromPhone(IEnumerable<CameraInfo> cameras) => cameras
        .OrderBy(c => Array.IndexOf(Order, c.Id) is var i && i >= 0 ? i : Order.Length)
        .Select(c => new CameraOption(c.Id, c.Name, c.Id switch
        {
            "back-ultrawide" => Loc.LensUltraWide,
            "back-wide" => Loc.LensWide,
            "back-tele" => Loc.LensTele,
            "front" => Loc.LensFront,
            _ => c.Name,
        }));
}

public sealed record QualityOption(int Width, int Height, int Fps, int BitrateKbps)
{
    public override string ToString() => $"{Height}p · {Fps} fps";
}

public sealed record StabilizationOption(string Id, string Label)
{
    public override string ToString() => Label;

    public static StabilizationOption For(string id) => new(id, id switch
    {
        StabilizationModes.Off => Loc.StabilizationOff,
        StabilizationModes.Standard => Loc.StabilizationStandard,
        StabilizationModes.Cinematic => Loc.StabilizationCinematic,
        _ => id,
    });
}

/// <summary>
/// Camera settings on the PC. Edits are sent to the phone as <see cref="Control"/>; the phone answers with a full
/// <see cref="CameraState"/>, which is mirrored back here. Must be used on the UI thread.
/// </summary>
public sealed class CameraControlsViewModel : ReactiveObject
{
    // Echoes of older states arrive while a slider is being dragged; ignore them for a moment after a local edit.
    private static readonly TimeSpan EchoGrace = TimeSpan.FromMilliseconds(700);

    private readonly Func<Control, Task> _send;
    // Where throttled sends and re-applies run: the UI thread in the app, virtual time in tests.
    private readonly IScheduler _ui;
    private readonly Subject<Unit> _localEdits = new();
    private bool _fromPhone;
    // Between a local edit and EchoGrace after the last one; phone states wait in _pending meanwhile.
    private bool _isEditing;
    private CameraState? _pending;

    private bool _isAvailable;
    private IReadOnlyList<CameraOption> _cameras = [];
    private CameraOption? _selectedCamera;
    private IReadOnlyList<QualityOption> _qualities = [];
    private QualityOption? _selectedQuality;
    private double _zoom = 1;
    private double _minZoom = 1;
    private double _maxZoom = 1;
    private double _exposureBias;
    private double _lensPosition = 0.5;
    private bool _isAutoFocus = true;
    private bool _supportsFocus;
    private bool _hasTorch;
    private bool _torch;
    private bool _mirror;
    private int _rotation;
    private bool _supportsWhiteBalance;
    private bool _isAutoWhiteBalance = true;
    private double _whiteBalanceTemperature = 5000;
    private double _whiteBalanceTint;
    private bool _supportsExposureLock;
    private bool _isExposureLocked;
    private IReadOnlyList<StabilizationOption> _stabilizationOptions = [];
    private StabilizationOption? _selectedStabilization;
    private IReadOnlyList<CameraInfo> _cameraInfos = [];

    /// <param name="ui">Scheduler of the UI thread (the one this view model is used on).</param>
    public CameraControlsViewModel(Func<Control, Task> send, IScheduler ui)
    {
        _send = send;
        _ui = ui;

        // Once the user has stopped editing, show the newest state from the phone.
        _localEdits.Throttle(EchoGrace, _ui).Subscribe(_ =>
        {
            _isEditing = false;
            if (_pending is { } state)
                ApplyState(state);
        });

        // Sliders: send at most every 80 ms while dragging.
        this.WhenAnyValue(x => x.Zoom).Skip(1).Where(_ => !_fromPhone).Do(_ => MarkLocalEdit())
            .Throttle(TimeSpan.FromMilliseconds(80), _ui)
            .Subscribe(value => Send(new Control { Zoom = value }));
        this.WhenAnyValue(x => x.ExposureBias).Skip(1).Where(_ => !_fromPhone).Do(_ => MarkLocalEdit())
            .Throttle(TimeSpan.FromMilliseconds(80), _ui)
            .Subscribe(value => Send(new Control { ExposureBias = value }));
        this.WhenAnyValue(x => x.LensPosition).Skip(1).Where(_ => !_fromPhone).Do(_ => MarkLocalEdit())
            .Throttle(TimeSpan.FromMilliseconds(80), _ui)
            .Subscribe(value => Send(new Control { FocusMode = "locked", LensPosition = value }));
        // Moving either white balance slider locks it at the chosen temperature and tint.
        this.WhenAnyValue(x => x.WhiteBalanceTemperature, x => x.WhiteBalanceTint).Skip(1).Where(_ => !_fromPhone).Do(_ => MarkLocalEdit())
            .Throttle(TimeSpan.FromMilliseconds(80), _ui)
            .Subscribe(value => Send(new Control
            {
                WhiteBalanceMode = WhiteBalanceModes.Locked,
                WhiteBalanceTemperature = Math.Round(value.Item1),
                WhiteBalanceTint = Math.Round(value.Item2),
            }));

        AutoFocusCommand = ReactiveCommand.Create(() => Send(new Control { FocusMode = "continuous" }));
        AutoWhiteBalanceCommand = ReactiveCommand.Create(() => Send(new Control { WhiteBalanceMode = WhiteBalanceModes.Auto }));
        RotateCommand = ReactiveCommand.Create(() => Send(new Control { Rotation = (Rotation + 90) % 360 }));
        // Sending is fire-and-forget; an error must not reach ReactiveUI's default handler, which crashes the app.
        AutoFocusCommand.ThrownExceptions.Subscribe(ReportSendError);
        AutoWhiteBalanceCommand.ThrownExceptions.Subscribe(ReportSendError);
        RotateCommand.ThrownExceptions.Subscribe(ReportSendError);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> AutoFocusCommand { get; }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RotateCommand { get; }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> AutoWhiteBalanceCommand { get; }

    // White balance

    public bool SupportsWhiteBalance { get => _supportsWhiteBalance; private set => this.RaiseAndSetIfChanged(ref _supportsWhiteBalance, value); }

    public bool IsAutoWhiteBalance { get => _isAutoWhiteBalance; private set => this.RaiseAndSetIfChanged(ref _isAutoWhiteBalance, value); }

    /// <summary>Kelvin, 2500 (warm light, picture turns bluer) to 8000 (daylight shade, picture turns warmer).</summary>
    public double WhiteBalanceTemperature
    {
        get => _whiteBalanceTemperature;
        set
        {
            this.RaiseAndSetIfChanged(ref _whiteBalanceTemperature, value);
            this.RaisePropertyChanged(nameof(WhiteBalanceText));
        }
    }

    /// <summary>Green (negative) to magenta (positive).</summary>
    public double WhiteBalanceTint
    {
        get => _whiteBalanceTint;
        set
        {
            this.RaiseAndSetIfChanged(ref _whiteBalanceTint, value);
            this.RaisePropertyChanged(nameof(WhiteBalanceTintText));
        }
    }

    public string WhiteBalanceText => $"{WhiteBalanceTemperature:0} K";

    public string WhiteBalanceTintText => $"{WhiteBalanceTint:+0;-0;0}";

    // Exposure lock

    public bool SupportsExposureLock { get => _supportsExposureLock; private set => this.RaiseAndSetIfChanged(ref _supportsExposureLock, value); }

    /// <summary>Locked exposure keeps brightness fixed; the bias slider has no effect then.</summary>
    public bool IsExposureLocked
    {
        get => _isExposureLocked;
        set
        {
            if (_isExposureLocked == value)
                return;
            this.RaiseAndSetIfChanged(ref _isExposureLocked, value);
            this.RaisePropertyChanged(nameof(CanAdjustExposure));
            if (!_fromPhone)
            {
                MarkLocalEdit();
                Send(new Control { ExposureMode = value ? ExposureModes.Locked : ExposureModes.Auto });
            }
        }
    }

    public bool CanAdjustExposure => !IsExposureLocked;

    // Stabilization

    public IReadOnlyList<StabilizationOption> StabilizationOptions
    {
        get => _stabilizationOptions;
        private set
        {
            this.RaiseAndSetIfChanged(ref _stabilizationOptions, value);
            this.RaisePropertyChanged(nameof(SupportsStabilization));
        }
    }

    /// <summary>The current format offers more than "off".</summary>
    public bool SupportsStabilization => StabilizationOptions.Count > 1;

    public StabilizationOption? SelectedStabilization
    {
        get => _selectedStabilization;
        set
        {
            if (Equals(_selectedStabilization, value))
                return;
            this.RaiseAndSetIfChanged(ref _selectedStabilization, value);
            if (!_fromPhone && value is not null)
            {
                MarkLocalEdit();
                Send(new Control { Stabilization = value.Id });
            }
        }
    }

    /// <summary>False until the phone has reported its cameras and current state.</summary>
    public bool IsAvailable { get => _isAvailable; private set => this.RaiseAndSetIfChanged(ref _isAvailable, value); }

    public IReadOnlyList<CameraOption> Cameras { get => _cameras; private set => this.RaiseAndSetIfChanged(ref _cameras, value); }

    public CameraOption? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (Equals(_selectedCamera, value))
                return;
            this.RaiseAndSetIfChanged(ref _selectedCamera, value);
            if (!_fromPhone && value is not null)
            {
                MarkLocalEdit();
                Send(new Control { CameraId = value.Id });
            }
        }
    }

    public IReadOnlyList<QualityOption> Qualities { get => _qualities; private set => this.RaiseAndSetIfChanged(ref _qualities, value); }

    public QualityOption? SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (Equals(_selectedQuality, value))
                return;
            this.RaiseAndSetIfChanged(ref _selectedQuality, value);
            if (!_fromPhone && value is not null)
            {
                MarkLocalEdit();
                Send(new Control { Width = value.Width, Height = value.Height, Fps = value.Fps, BitrateKbps = value.BitrateKbps });
            }
        }
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            this.RaiseAndSetIfChanged(ref _zoom, value);
            this.RaisePropertyChanged(nameof(ZoomText));
        }
    }

    public double MinZoom { get => _minZoom; private set => this.RaiseAndSetIfChanged(ref _minZoom, value); }

    public double MaxZoom { get => _maxZoom; private set => this.RaiseAndSetIfChanged(ref _maxZoom, value); }

    public bool CanZoom => MaxZoom > MinZoom;

    public string ZoomText => $"{Zoom:0.0}×";

    public double ExposureBias
    {
        get => _exposureBias;
        set
        {
            this.RaiseAndSetIfChanged(ref _exposureBias, value);
            this.RaisePropertyChanged(nameof(ExposureText));
        }
    }

    public string ExposureText => $"{ExposureBias:+0.0;-0.0;0} EV";

    public double LensPosition { get => _lensPosition; set => this.RaiseAndSetIfChanged(ref _lensPosition, value); }

    public bool IsAutoFocus { get => _isAutoFocus; private set => this.RaiseAndSetIfChanged(ref _isAutoFocus, value); }

    public bool SupportsFocus { get => _supportsFocus; private set => this.RaiseAndSetIfChanged(ref _supportsFocus, value); }

    public bool HasTorch { get => _hasTorch; private set => this.RaiseAndSetIfChanged(ref _hasTorch, value); }

    public bool Torch
    {
        get => _torch;
        set
        {
            if (_torch == value)
                return;
            this.RaiseAndSetIfChanged(ref _torch, value);
            if (!_fromPhone)
            {
                MarkLocalEdit();
                Send(new Control { Torch = value });
            }
        }
    }

    public bool Mirror
    {
        get => _mirror;
        set
        {
            if (_mirror == value)
                return;
            this.RaiseAndSetIfChanged(ref _mirror, value);
            if (!_fromPhone)
            {
                MarkLocalEdit();
                Send(new Control { Mirror = value });
            }
        }
    }

    public int Rotation
    {
        get => _rotation;
        private set
        {
            this.RaiseAndSetIfChanged(ref _rotation, value);
            this.RaisePropertyChanged(nameof(RotationText));
        }
    }

    public string RotationText => $"{Rotation}°";

    public void ApplyCapabilities(Capabilities capabilities)
    {
        _cameraInfos = capabilities.Cameras;
        RunFromPhone(() =>
        {
            Cameras = [.. CameraOption.FromPhone(capabilities.Cameras)];
            Qualities = [.. capabilities.Presets
                .SelectMany(p => p.Fps.Select(fps => new QualityOption(p.Width, p.Height, fps, DefaultBitrate(p.Height, fps))))
                .OrderBy(q => q.Height).ThenBy(q => q.Fps)];
        });
        if (_pending is { } state)
            ApplyState(state);
    }

    public void ApplyState(CameraState state)
    {
        _pending = state;
        if (_isEditing)
        {
            // Applied once the edit settles (see the constructor).
            return;
        }

        RunFromPhone(() =>
        {
            var camera = _cameraInfos.FirstOrDefault(c => c.Id == state.CameraId);
            SelectedCamera = Cameras.FirstOrDefault(c => c.Id == state.CameraId);
            SelectedQuality = Qualities.FirstOrDefault(q => q.Width == state.Width && q.Height == state.Height && q.Fps == state.Fps)
                              ?? Qualities.FirstOrDefault(q => q.Width == state.Width && q.Height == state.Height);
            MinZoom = camera?.MinZoom ?? 1;
            MaxZoom = camera?.MaxZoom ?? 1;
            Zoom = Math.Clamp(state.Zoom, MinZoom, MaxZoom);
            ExposureBias = state.ExposureBias;
            LensPosition = state.LensPosition;
            IsAutoFocus = state.FocusMode != "locked";
            SupportsFocus = camera?.SupportsFocus ?? false;
            HasTorch = camera?.HasTorch ?? false;
            Torch = state.Torch;
            Mirror = state.Mirror;
            Rotation = state.Rotation;

            // Older phone apps send neither the capability nor the state: the controls stay hidden.
            SupportsWhiteBalance = camera?.SupportsWhiteBalance == true && state.WhiteBalanceMode is not null;
            IsAutoWhiteBalance = state.WhiteBalanceMode != WhiteBalanceModes.Locked;
            WhiteBalanceTemperature = Math.Clamp(state.WhiteBalanceTemperature ?? 5000, 2500, 8000);
            WhiteBalanceTint = Math.Clamp(state.WhiteBalanceTint ?? 0, -100, 100);
            SupportsExposureLock = camera?.SupportsExposureLock == true && state.ExposureMode is not null;
            IsExposureLocked = state.ExposureMode == ExposureModes.Locked;
            var modes = state.StabilizationModes ?? [];
            if (!modes.SequenceEqual(StabilizationOptions.Select(o => o.Id)))
                StabilizationOptions = [.. modes.Select(StabilizationOption.For)];
            SelectedStabilization = StabilizationOptions.FirstOrDefault(o => o.Id == (state.Stabilization ?? StabilizationModes.Off));

            this.RaisePropertyChanged(nameof(CanZoom));
            IsAvailable = Cameras.Count > 0;
        });
    }

    public void Reset()
    {
        _pending = null;
        _cameraInfos = [];
        RunFromPhone(() =>
        {
            IsAvailable = false;
            Cameras = [];
            Qualities = [];
            SelectedCamera = null;
            SelectedQuality = null;
            SupportsWhiteBalance = false;
            SupportsExposureLock = false;
            StabilizationOptions = [];
            SelectedStabilization = null;
        });
    }

    // Same presets as the phone's own quality menu.
    private static int DefaultBitrate(int height, int fps) => (height, fps) switch
    {
        (<= 720, <= 30) => 4000,
        (<= 720, _) => 6000,
        (_, <= 30) => 8000,
        _ => 12000,
    };

    private void MarkLocalEdit()
    {
        _isEditing = true;
        _localEdits.OnNext(Unit.Default);
    }

    private static void ReportSendError(Exception ex) => Trace.TraceWarning($"HitCam: camera control not sent: {ex.Message}");

    private void RunFromPhone(Action update)
    {
        _fromPhone = true;
        try
        {
            update();
        }
        finally
        {
            _fromPhone = false;
        }
    }

    private void Send(Control control)
    {
        try
        {
            _ = _send(control).ContinueWith(
                t => ReportSendError(t.Exception!.GetBaseException()), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            ReportSendError(ex);
        }
    }
}

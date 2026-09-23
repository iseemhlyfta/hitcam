using System.Reactive.Concurrency;
using System.Reactive.Linq;
using HitCam.Core.Protocol;
using ReactiveUI;

namespace HitCam.Desktop.ViewModels;

public sealed record CameraOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record QualityOption(int Width, int Height, int Fps, int BitrateKbps)
{
    public override string ToString() => $"{Height}p {Fps} fps";
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
    // Created on the UI thread (see the class summary), so throttled sends and re-applies land back on it.
    private readonly IScheduler _ui = new SynchronizationContextScheduler(SynchronizationContext.Current ?? new SynchronizationContext());
    private bool _fromPhone;
    private DateTime _lastLocalEdit;
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
    private IReadOnlyList<CameraInfo> _cameraInfos = [];

    public CameraControlsViewModel(Func<Control, Task> send)
    {
        _send = send;

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

        AutoFocusCommand = ReactiveCommand.Create(() => Send(new Control { FocusMode = "continuous" }));
        RotateCommand = ReactiveCommand.Create(() => Send(new Control { Rotation = (Rotation + 90) % 360 }));
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> AutoFocusCommand { get; }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RotateCommand { get; }

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
            Cameras = [.. capabilities.Cameras.Select(c => new CameraOption(c.Id, c.Name))];
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
        if (DateTime.UtcNow - _lastLocalEdit < EchoGrace)
        {
            // Applied once the edit settles (see MarkLocalEdit).
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
        _lastLocalEdit = DateTime.UtcNow;
        // Re-apply the newest phone state once the user has stopped editing.
        _ui.Schedule(EchoGrace + TimeSpan.FromMilliseconds(50), () =>
        {
            if (DateTime.UtcNow - _lastLocalEdit >= EchoGrace && _pending is { } state)
                ApplyState(state);
        });
    }

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

    private void Send(Control control) => _ = _send(control);
}

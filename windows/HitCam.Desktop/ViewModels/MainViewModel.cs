using System.Net.Sockets;
using System.Reactive;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HitCam.Core.Pairing;
using HitCam.Core.Protocol;
using HitCam.Core.Server;
using HitCam.Desktop.Services;
using ReactiveUI;

namespace HitCam.Desktop.ViewModels;

public sealed class MainViewModel : ReactiveObject, IAsyncDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HitCamServer _server;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _previewTimer;
    private readonly VideoPipeline _pipeline = new();
    private readonly VirtualCamera _camera = new();
    private long _framesInWindow;
    private long _bytesInWindow;
    private long _lastLatencyMicros = -1;

    // Preview: two bitmaps used in turn, so every new frame is a new Source and the Image redraws.
    private readonly WriteableBitmap?[] _previewBuffers = new WriteableBitmap?[2];
    private int _nextPreviewBuffer;
    private ulong _lastPreviewFrame;

    private string _statusText = "";
    private string? _pin;
    private string _pairingText = "";
    private bool _isConnected;
    private Bitmap? _qrCode;
    private string _primaryAddress = "";
    private string _otherAddresses = "";
    private string _deviceName = "";
    private string _deviceSubtitle = "";
    private string _liveText = "";
    private string _streamText = "—";
    private string _receivedText = "—";
    private string _latencyText = "—";
    private string _phoneText = "—";
    private string _cameraTitle = "";
    private string _cameraDetail = "";
    private bool _isCameraReady;
    private string _installCameraLabel = "";
    private bool _canInstallCamera;
    private WriteableBitmap? _preview;
    private bool _isFullScreen;

    public MainViewModel()
    {
        _server = new HitCamServer(
            new HitCamServerOptions { Port = Program.PortOverride ?? _settings.Port, ServerId = _settings.ServerId },
            new FilePairingStore(AppPaths.PairedDevices));

        _server.PairingStarted += p => Dispatcher.UIThread.Post(() =>
        {
            PairingText = Loc.PairingRequest(p.DeviceName, p.RemoteEndPoint.Address.ToString());
            Pin = p.Pin;
        });
        _server.PairingEnded += () => Dispatcher.UIThread.Post(() => Pin = null);
        _server.Connected += d => Dispatcher.UIThread.Post(() =>
        {
            DeviceName = d.DeviceName;
            DeviceSubtitle = Loc.DeviceSubtitle(d.RemoteEndPoint.Address.ToString());
            // Frames decoded before this connection belong to the previous one.
            _lastPreviewFrame = _pipeline.PreviewInfo().Frame;
            IsConnected = true;
            _previewTimer!.Start();
        });
        _server.Disconnected += (_, reason) => Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            IsFullScreen = false;
            _previewTimer!.Stop();
            Preview = null;
            StatusText = Loc.Disconnected(reason);
            StreamText = ReceivedText = LatencyText = PhoneText = "—";
            LiveText = "";
        });
        _server.Disconnected += (_, _) => _pipeline.ClearSignal();

        Controls = new CameraControlsViewModel(control => _server.SendControlAsync(control));
        _server.CapabilitiesReceived += c => Dispatcher.UIThread.Post(() => Controls.ApplyCapabilities(c));
        _server.CameraStateReceived += s => Dispatcher.UIThread.Post(() => Controls.ApplyState(s));
        _server.Disconnected += (_, _) => Dispatcher.UIThread.Post(Controls.Reset);
        _pipeline.KeyframeNeeded += () => _ = _server.RequestKeyframeAsync();
        _server.StreamConfigReceived += c => Dispatcher.UIThread.Post(() =>
        {
            var codec = c.Codec.ToLowerInvariant() switch { "h264" => "H.264", "hevc" => "HEVC", _ => c.Codec.ToUpperInvariant() };
            StreamText = $"{codec} · {c.Width}×{c.Height}";
            LiveText = $"LIVE · {Math.Min(c.Width, c.Height)}p · {c.Fps} fps";
        });
        _server.StatusReceived += s => Dispatcher.UIThread.Post(() =>
            PhoneText = $"{s.Battery:P0}{(s.Charging ? $" · {Loc.Charging}" : "")}");
        _server.FrameReceived += f =>
        {
            Interlocked.Increment(ref _framesInWindow);
            Interlocked.Add(ref _bytesInWindow, f.Data.Length);
            if (f.LatencyMicros is { } latency)
                Interlocked.Exchange(ref _lastLatencyMicros, latency);
            _pipeline.Push(f);
        };

        DisconnectCommand = ReactiveCommand.Create(() => _server.Kick());
        CancelPairingCommand = ReactiveCommand.Create(() => _server.Kick());
        InstallCameraCommand = ReactiveCommand.CreateFromTask(InstallCameraAsync);
        ToggleFullScreenCommand = ReactiveCommand.Create(() => { IsFullScreen = !IsFullScreen; });
        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStats());
        _previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdatePreview());
    }

    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }

    public ReactiveCommand<Unit, Unit> CancelPairingCommand { get; }

    public ReactiveCommand<Unit, Unit> InstallCameraCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }

    /// <summary>Phone camera settings, editable from the PC.</summary>
    public CameraControlsViewModel Controls { get; }

    // Screens: exactly one of IsWaiting / IsPairing / IsConnected is true.

    public bool IsWaiting => !IsConnected && !IsPairing;

    public bool IsPairing => Pin is not null;

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isConnected, value);
            this.RaisePropertyChanged(nameof(IsWaiting));
        }
    }

    /// <summary>Last disconnect reason or a server error, shown while waiting.</summary>
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    // Waiting

    public Bitmap? QrCode { get => _qrCode; private set => this.RaiseAndSetIfChanged(ref _qrCode, value); }

    public string PrimaryAddress { get => _primaryAddress; private set => this.RaiseAndSetIfChanged(ref _primaryAddress, value); }

    public string OtherAddresses
    {
        get => _otherAddresses;
        private set
        {
            this.RaiseAndSetIfChanged(ref _otherAddresses, value);
            this.RaisePropertyChanged(nameof(HasOtherAddresses));
        }
    }

    public bool HasOtherAddresses => OtherAddresses.Length > 0;

    // Pairing

    public string? Pin
    {
        get => _pin;
        private set
        {
            this.RaiseAndSetIfChanged(ref _pin, value);
            this.RaisePropertyChanged(nameof(IsPairing));
            this.RaisePropertyChanged(nameof(IsWaiting));
            this.RaisePropertyChanged(nameof(PinFirstHalf));
            this.RaisePropertyChanged(nameof(PinSecondHalf));
        }
    }

    public string[] PinFirstHalf => Pin is { Length: 6 } pin ? [.. pin[..3].Select(c => c.ToString())] : [];

    public string[] PinSecondHalf => Pin is { Length: 6 } pin ? [.. pin[3..].Select(c => c.ToString())] : [];

    public string PairingText { get => _pairingText; private set => this.RaiseAndSetIfChanged(ref _pairingText, value); }

    // Streaming

    public string DeviceName { get => _deviceName; private set => this.RaiseAndSetIfChanged(ref _deviceName, value); }

    public string DeviceSubtitle { get => _deviceSubtitle; private set => this.RaiseAndSetIfChanged(ref _deviceSubtitle, value); }

    public string LiveText { get => _liveText; private set => this.RaiseAndSetIfChanged(ref _liveText, value); }

    public string StreamText { get => _streamText; private set => this.RaiseAndSetIfChanged(ref _streamText, value); }

    public string ReceivedText { get => _receivedText; private set => this.RaiseAndSetIfChanged(ref _receivedText, value); }

    public string LatencyText { get => _latencyText; private set => this.RaiseAndSetIfChanged(ref _latencyText, value); }

    public string PhoneText { get => _phoneText; private set => this.RaiseAndSetIfChanged(ref _phoneText, value); }

    public WriteableBitmap? Preview
    {
        get => _preview;
        private set
        {
            this.RaiseAndSetIfChanged(ref _preview, value);
            this.RaisePropertyChanged(nameof(HasPreview));
        }
    }

    public bool HasPreview => Preview is not null;

    /// <summary>Only the preview is shown; the view makes the window full screen.</summary>
    public bool IsFullScreen
    {
        get => _isFullScreen;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFullScreen, value);
            this.RaisePropertyChanged(nameof(IsNotFullScreen));
        }
    }

    public bool IsNotFullScreen => !IsFullScreen;

    // Virtual camera

    public string CameraTitle { get => _cameraTitle; private set => this.RaiseAndSetIfChanged(ref _cameraTitle, value); }

    public string CameraDetail { get => _cameraDetail; private set => this.RaiseAndSetIfChanged(ref _cameraDetail, value); }

    /// <summary>The camera is installed, current and running.</summary>
    public bool IsCameraReady
    {
        get => _isCameraReady;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isCameraReady, value);
            this.RaisePropertyChanged(nameof(IsCameraAttention));
        }
    }

    public bool IsCameraAttention => !IsCameraReady;

    public string InstallCameraLabel { get => _installCameraLabel; private set => this.RaiseAndSetIfChanged(ref _installCameraLabel, value); }

    public bool CanInstallCamera { get => _canInstallCamera; private set => this.RaiseAndSetIfChanged(ref _canInstallCamera, value); }

    public void Start()
    {
        try
        {
            _server.Start();
        }
        catch (SocketException ex)
        {
            StatusText = Loc.ServerFailed(ex.Message);
        }

        RefreshAddresses();
        RefreshCamera();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) =>
            Dispatcher.UIThread.Post(RefreshAddresses);
        _statsTimer.Start();
    }

    private void RefreshAddresses()
    {
        var addresses = LanAddresses.Get();
        if (addresses.Count == 0)
        {
            PrimaryAddress = Loc.NoNetwork;
            OtherAddresses = "";
            QrCode = null;
            return;
        }

        PrimaryAddress = $"{addresses[0]}:{_server.Port}";
        OtherAddresses = addresses.Count > 1
            ? Loc.OtherAddresses(string.Join(", ", addresses.Skip(1).Select(a => $"{a}:{_server.Port}")))
            : "";
        var name = Uri.EscapeDataString(Environment.MachineName);
        QrCode = QrImage.Create($"{ProtocolInfo.UriScheme}://{addresses[0]}:{_server.Port}?id={_settings.ServerId}&name={name}");
    }

    private void RefreshCamera()
    {
        var setup = VirtualCamera.GetSetup();
        if (setup is CameraSetup.Unavailable)
        {
            SetCameraStatus(false, Loc.CameraUnavailableTitle, Loc.CameraUnavailableDetail, null);
            return;
        }
        if (setup is CameraSetup.NotInstalled)
        {
            SetCameraStatus(false, Loc.CameraNotInstalledTitle, Loc.CameraNotInstalledDetail, Loc.InstallCamera);
            return;
        }

        var hr = _camera.Start();
        if (hr < 0)
            SetCameraStatus(false, Loc.CameraFailedTitle, Loc.CameraFailedDetail($"0x{hr:X8}"), Loc.ReinstallCamera);
        else if (setup is CameraSetup.Outdated)
            SetCameraStatus(false, Loc.CameraOutdatedTitle, Loc.CameraOutdatedDetail, Loc.UpdateCamera);
        else
            SetCameraStatus(true, Loc.CameraReadyTitle, Loc.CameraReadyDetail, null);
    }

    private void SetCameraStatus(bool ready, string title, string detail, string? action)
    {
        IsCameraReady = ready;
        CameraTitle = title;
        CameraDetail = detail;
        CanInstallCamera = action is not null;
        InstallCameraLabel = action ?? "";
    }

    private async Task InstallCameraAsync()
    {
        _camera.Stop();
        SetCameraStatus(false, Loc.CameraInstallingTitle, Loc.CameraInstallingDetail, null);
        var installed = await VirtualCamera.InstallAsync();
        RefreshCamera();
        if (!installed && !_camera.IsRunning)
            SetCameraStatus(false, Loc.CameraInstallFailedTitle, Loc.CameraInstallFailedDetail, Loc.InstallCamera);
    }

    private void UpdatePreview()
    {
        var (width, height, frame) = _pipeline.PreviewInfo();
        if (width == 0 || frame == _lastPreviewFrame)
            return;

        var bitmap = _previewBuffers[_nextPreviewBuffer];
        if (bitmap is null || bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height)
        {
            bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _previewBuffers[_nextPreviewBuffer] = bitmap;
        }

        using (var buffer = bitmap.Lock())
        {
            if (!_pipeline.CopyPreview(buffer.Address, buffer.RowBytes, width, height))
                return;
        }

        _lastPreviewFrame = frame;
        _nextPreviewBuffer ^= 1;
        Preview = bitmap;
    }

    private void UpdateStats()
    {
        if (_pipeline.Error is { } error)
            SetCameraStatus(false, Loc.CameraFailedTitle, Loc.DecoderFailed(error), null);

        if (!IsConnected)
            return;

        var frames = Interlocked.Exchange(ref _framesInWindow, 0);
        var bytes = Interlocked.Exchange(ref _bytesInWindow, 0);
        ReceivedText = $"{frames} fps · {bytes * 8 / 1_000_000.0:0.0} {Loc.Megabits}";

        var latency = Interlocked.Read(ref _lastLatencyMicros);
        var rtt = _server.RoundTripMicros;
        LatencyText = (latency >= 0 ? $"{latency / 1000.0:0} {Loc.Milliseconds}" : "—")
                      + (rtt is { } r ? $" · RTT {r / 1000.0:0} {Loc.Milliseconds}" : "");
    }

    public async ValueTask DisposeAsync()
    {
        _statsTimer.Stop();
        _previewTimer.Stop();
        await _server.DisposeAsync();
        _pipeline.Dispose();
        _camera.Dispose();
    }
}

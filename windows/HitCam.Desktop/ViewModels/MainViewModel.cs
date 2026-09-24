using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive.Linq;
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
using ReactiveUI.Avalonia;

namespace HitCam.Desktop.ViewModels;

public sealed class MainViewModel : ReactiveObject, IAsyncDisposable
{
    private AppSettings _settings = AppSettings.Load();
    private readonly HitCamServer _server;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _previewTimer;
    // Network changes come in bursts (an adapter going up raises several); addresses are read once it settles.
    private readonly DispatcherTimer _addressTimer;
    private NetworkAddressChangedEventHandler? _networkChanged;
    private int _disposed;
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
    private bool _isDenoiseAvailable;
    private bool _isDenoiseEnabled;
    private DenoiseModeOption _selectedDenoiseMode;
    private string _denoiseStatus = "";

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
            ReleasePreviewBuffers();
            StatusText = Loc.Disconnected(reason);
            StreamText = ReceivedText = LatencyText = PhoneText = "—";
            LiveText = "";
        });
        _server.Disconnected += (_, _) => _pipeline.ClearSignal();

        Controls = new CameraControlsViewModel(control => _server.SendControlAsync(control), AvaloniaScheduler.Instance);
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
        OpenDenoiseDownloadCommand = ReactiveCommand.Create(() =>
        {
            Process.Start(new ProcessStartInfo(NvidiaVideoEffectsDownload) { UseShellExecute = true })?.Dispose();
        });

        // An unobserved command error would crash the app; show it where the user clicked instead.
        DisconnectCommand.ThrownExceptions.Subscribe(ex => StatusText = Loc.ActionFailed(ex.Message));
        CancelPairingCommand.ThrownExceptions.Subscribe(ex => StatusText = Loc.ActionFailed(ex.Message));
        InstallCameraCommand.ThrownExceptions.Subscribe(ex =>
            SetCameraStatus(false, Loc.CameraInstallFailedTitle, Loc.CameraInstallError(ex.Message), Loc.InstallCamera));
        ToggleFullScreenCommand.ThrownExceptions.Subscribe(ex => Trace.TraceWarning($"Full screen: {ex.Message}"));
        OpenDenoiseDownloadCommand.ThrownExceptions.Subscribe(ex => DenoiseStatus = Loc.ActionFailed(ex.Message));

        _isDenoiseEnabled = _settings.DenoiseEnabled;
        _selectedDenoiseMode = DenoiseModes.FirstOrDefault(o => o.Key == _settings.DenoiseMode) ?? DenoiseModes[1];
        // Property-initialized timers: the constructor taking a callback also starts the timer.
        _statsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => UpdateStats();
        _previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _previewTimer.Tick += (_, _) => UpdatePreview();
        _addressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _addressTimer.Tick += (_, _) =>
        {
            _addressTimer.Stop();
            RefreshAddresses();
        };
    }

    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }

    public ReactiveCommand<Unit, Unit> CancelPairingCommand { get; }

    public ReactiveCommand<Unit, Unit> InstallCameraCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenDenoiseDownloadCommand { get; }

    // AI noise removal (NVIDIA Video Effects SDK, on the PC's RTX GPU)

    private const string NvidiaVideoEffectsDownload = "https://www.nvidia.com/en-us/geforce/broadcasting/broadcast-sdk/resources/";

    /// <summary>The NVIDIA runtime is installed; checked in the background at start.</summary>
    public bool IsDenoiseAvailable
    {
        get => _isDenoiseAvailable;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isDenoiseAvailable, value);
            this.RaisePropertyChanged(nameof(IsDenoiseMissing));
        }
    }

    public bool IsDenoiseMissing => !IsDenoiseAvailable;

    public bool IsDenoiseEnabled
    {
        get => _isDenoiseEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _isDenoiseEnabled, value);
            ApplyDenoise();
            SaveDenoiseSettings();
            if (!value)
                DenoiseStatus = "";
        }
    }

    /// <summary>The three buttons: fast, general, maximum.</summary>
    public IReadOnlyList<DenoiseModeOption> DenoiseModes { get; } =
    [
        new("fast", DenoiseMode.Fast, Loc.DenoiseFast, Loc.DenoiseFastHint),
        new("general", DenoiseMode.General, Loc.DenoiseGeneral, Loc.DenoiseGeneralHint),
        new("maximum", DenoiseMode.Maximum, Loc.DenoiseMaximum, Loc.DenoiseMaximumHint),
    ];

    public DenoiseModeOption SelectedDenoiseMode
    {
        get => _selectedDenoiseMode;
        set
        {
            // The segmented list briefly reports "nothing selected" while it rebuilds; keep the last choice then.
            if (value is null || value == _selectedDenoiseMode)
                return;
            this.RaiseAndSetIfChanged(ref _selectedDenoiseMode, value);
            ApplyDenoise();
            SaveDenoiseSettings();
        }
    }

    /// <summary>Loading / noise level / time per frame / error, while enabled.</summary>
    public string DenoiseStatus { get => _denoiseStatus; private set => this.RaiseAndSetIfChanged(ref _denoiseStatus, value); }

    private void ApplyDenoise() =>
        _pipeline.SetDenoise(IsDenoiseAvailable && IsDenoiseEnabled ? SelectedDenoiseMode.Mode : DenoiseMode.Off);

    private void SaveDenoiseSettings()
    {
        _settings = _settings with { DenoiseEnabled = IsDenoiseEnabled, DenoiseMode = SelectedDenoiseMode.Key };
        _settings.Save();
    }

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
        _networkChanged = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _addressTimer.Stop();
            _addressTimer.Start();
        });
        NetworkChange.NetworkAddressChanged += _networkChanged;
        // Loading the NVIDIA libraries takes a moment; the switch appears once they are found.
        _ = Task.Run(VideoPipeline.IsDenoiseAvailable).ContinueWith(
            t => Dispatcher.UIThread.Post(() =>
            {
                IsDenoiseAvailable = t.Result;
                ApplyDenoise();
            }),
            TaskScheduler.Default);
        _statsTimer.Start();
    }

    private void RefreshAddresses()
    {
        var addresses = LanAddresses.Get();
        if (addresses.Count == 0)
        {
            PrimaryAddress = Loc.NoNetwork;
            OtherAddresses = "";
            ReplaceQrCode(null);
            return;
        }

        PrimaryAddress = $"{addresses[0]}:{_server.Port}";
        OtherAddresses = addresses.Count > 1
            ? Loc.OtherAddresses(string.Join(", ", addresses.Skip(1).Select(a => $"{a}:{_server.Port}")))
            : "";
        var name = Uri.EscapeDataString(Environment.MachineName);
        ReplaceQrCode(QrImage.Create($"{ProtocolInfo.UriScheme}://{addresses[0]}:{_server.Port}?id={_settings.ServerId}&name={name}"));
    }

    private void ReplaceQrCode(Bitmap? qrCode)
    {
        var old = QrCode;
        QrCode = qrCode;
        old?.Dispose();
    }

    private void RefreshCamera()
    {
        // A second instance for debugging (--port) must not add a second "HitCam" camera.
        if (Program.PortOverride is not null)
        {
            SetCameraStatus(false, Loc.DebugInstanceTitle, Loc.DebugInstanceDetail, null);
            return;
        }

        var setup = VirtualCamera.GetSetup();
        if (setup is CameraSetup.Unavailable)
        {
            SetCameraStatus(false, Loc.CameraUnavailableTitle, Loc.CameraUnavailableDetail, null);
            return;
        }
        if (setup is CameraSetup.Tampered)
        {
            SetCameraStatus(false, Loc.CameraTamperedTitle, Loc.CameraTamperedDetail, null);
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
        bool installed;
        try
        {
            installed = await VirtualCamera.InstallAsync();
        }
        catch (Exception ex)
        {
            RefreshCamera();
            if (!_camera.IsRunning)
                SetCameraStatus(false, Loc.CameraInstallFailedTitle, Loc.CameraInstallError(ex.Message), Loc.InstallCamera);
            return;
        }
        RefreshCamera();
        if (!installed && !_camera.IsRunning && CanInstallCamera)
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
            // The one on screen is the other buffer, so this one can go.
            bitmap?.Dispose();
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

        if (IsDenoiseAvailable && IsDenoiseEnabled)
        {
            var stats = _pipeline.DenoiseStats();
            DenoiseStatus = stats.Error != 0 ? Loc.DenoiseFailed(stats.Error)
                : stats.Noise is not { } noise ? Loc.DenoiseLoading
                // Below ~2% the picture is clean enough that it is passed through untouched.
                : stats.Amount < 0.02f ? Loc.DenoiseIdle(noise)
                : stats.Milliseconds is { } ms ? Loc.DenoiseActive(noise, stats.Amount, ms)
                : Loc.DenoiseLoading;
        }

        var frames = Interlocked.Exchange(ref _framesInWindow, 0);
        var bytes = Interlocked.Exchange(ref _bytesInWindow, 0);
        ReceivedText = $"{frames} fps · {bytes * 8 / 1_000_000.0:0.0} {Loc.Megabits}";

        var latency = Interlocked.Read(ref _lastLatencyMicros);
        var rtt = _server.RoundTripMicros;
        LatencyText = (latency >= 0 ? $"{latency / 1000.0:0} {Loc.Milliseconds}" : "—")
                      + (rtt is { } r ? $" · RTT {r / 1000.0:0} {Loc.Milliseconds}" : "");
    }

    /// <summary>Called with <see cref="Preview"/> already cleared.</summary>
    private void ReleasePreviewBuffers()
    {
        for (var i = 0; i < _previewBuffers.Length; i++)
        {
            _previewBuffers[i]?.Dispose();
            _previewBuffers[i] = null;
        }
        _nextPreviewBuffer = 0;
    }

    /// <summary>
    /// App exit, on the UI thread, which the caller blocks: the network and native teardown runs on the thread pool
    /// and is given at most <paramref name="timeout"/>, so a stuck connection can never keep a windowless process alive.
    /// </summary>
    public void Shutdown(TimeSpan timeout)
    {
        StopOnUiThread();
        try
        {
            if (!Task.Run(DisposeCoreAsync).Wait(timeout))
                Trace.TraceWarning("HitCam: shutdown timed out; exiting anyway.");
        }
        catch (AggregateException ex)
        {
            Trace.TraceWarning($"HitCam: shutdown failed: {ex.InnerException?.Message}");
        }
    }

    /// <summary>Call on the UI thread.</summary>
    public async ValueTask DisposeAsync()
    {
        StopOnUiThread();
        await DisposeCoreAsync().ConfigureAwait(false);
    }

    private void StopOnUiThread()
    {
        _statsTimer.Stop();
        _previewTimer.Stop();
        _addressTimer.Stop();
        if (_networkChanged is not null)
        {
            NetworkChange.NetworkAddressChanged -= _networkChanged;
            _networkChanged = null;
        }
    }

    // No UI thread needed from here on.
    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _server.DisposeAsync().ConfigureAwait(false);
        _pipeline.Dispose();
        _camera.Dispose();
    }
}

/// <param name="Key">Stored in settings.</param>
/// <param name="Hint">What the mode does, shown under the buttons.</param>
public sealed record DenoiseModeOption(string Key, DenoiseMode Mode, string Label, string Hint)
{
    public override string ToString() => Label;
}

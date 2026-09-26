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
using HitCam.Vision;
using HitCam.Vision.Faces;
using HitCam.Vision.Hands;
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
    // A debug instance (--port) shows the preview only: the camera belongs to the real instance.
    private readonly VideoPipeline _pipeline = new(previewOnly: Program.PortOverride is not null);
    private readonly VirtualCamera _camera = new();
    private readonly VisionEngine _vision;
    private readonly HandEngine _hands;
    // Read by the hand tracking thread: shots and the hand scene go to the camera straight from there.
    private volatile bool _shotsToCamera;
    // The settings the camera scene is built with while tracking runs; null: nothing for the camera.
    private volatile HandSettings? _handsToCamera;
    private bool _handSceneSent;
    private bool _threadsWereOn;
    private readonly FaceEngine _faces;
    // The settings the camera regions are built with while face hiding runs; null: nothing for the camera.
    private volatile FaceSettings? _facesToCamera;
    private bool _faceRegionsSent;
    private readonly CameraOverlay _cameraOverlay;
    // The phone's stream size as width << 32 | height (0 before the first config); read by the analysis thread.
    private long _streamSize;
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
        // Applied to the pipeline right away, so the first decoder already starts with the saved settings.
        Processing = new ProcessingViewModel(
            _settings.Processing,
            settings => ApplyProcessing(settings),
            settings =>
            {
                _settings = _settings with { Processing = settings };
                _settings.Save();
            },
            AvaloniaScheduler.Instance);

        // Object analysis pulls preview frames itself, only when it is ready for one.
        _cameraOverlay = new CameraOverlay(boxes => _pipeline.SetOverlay(boxes));
        _vision = new VisionEngine(_pipeline.CopyPreviewTo);
        Vision = new VisionViewModel(
            _settings.Vision,
            ModelCatalog.Scan,
            _ => ApplyVision(),
            settings =>
            {
                _settings = _settings with { Vision = settings };
                _settings.Save();
            },
            AvaloniaScheduler.Instance);
        _vision.StatusChanged += s => Dispatcher.UIThread.Post(() => Vision.ShowStatus(s));
        _vision.ResultReady += r =>
        {
            // Burn-in straight from the analysis thread; the preview overlay on the UI thread.
            _cameraOverlay.FrameHeight = CameraFrameHeight(r.FrameWidth, r.FrameHeight);
            _cameraOverlay.Show(r.Tracks);
            Dispatcher.UIThread.Post(() => Vision.ShowResult(r));
        };
        Vision.WhenAnyValue(v => v.IsActive).Subscribe(_ => this.RaisePropertyChanged(nameof(ShowDetections)));

        // Hand tracking: its own thread and frames, independent of object analysis.
        _hands = new HandEngine(_pipeline.CopyPreviewTo);
        Hands = new HandsViewModel(
            _settings.Hands,
            () => HandModelFiles.Find() is not null,
            _ => ApplyHands(),
            settings =>
            {
                _settings = _settings with { Hands = settings };
                _settings.Save();
            },
            AvaloniaScheduler.Instance);
        _hands.StatusChanged += s => Dispatcher.UIThread.Post(() => Hands.ShowStatus(s));
        _hands.ResultReady += r =>
        {
            SendHandScene(r);
            if (_shotsToCamera)
            {
                foreach (var shot in r.Shots)
                {
                    DiagnosticLog.Write(FormattableString.Invariant(
                        $"shot: hand {shot.HandId} at {shot.Muzzle.X:0.00},{shot.Muzzle.Y:0.00} size {shot.Size:0.00}, frame {r.FrameWidth}x{r.FrameHeight}"));
                    _pipeline.Shot(HitCamShot.From(shot));
                }
            }
            Dispatcher.UIThread.Post(() => Hands.ShowResult(r));
        };
        Hands.WhenAnyValue(h => h.IsActive, h => h.ShowPoints).Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(ShowHands));
            this.RaisePropertyChanged(nameof(ShowHandPoints));
        });

        // Face hiding: its own thread and frames too; clicks in the preview go to the engine.
        _faces = new FaceEngine(_pipeline.CopyPreviewTo);
        Faces = new FacesViewModel(
            _settings.Faces,
            () => FaceModelFiles.Find() is not null,
            _ => ApplyFaces(),
            settings =>
            {
                _settings = _settings with { Faces = settings };
                _settings.Save();
            },
            _faces.Toggle,
            _faces.ForgetPeople,
            AvaloniaScheduler.Instance);
        _faces.StatusChanged += s => Dispatcher.UIThread.Post(() => Faces.ShowStatus(s));
        _faces.ResultReady += r =>
        {
            SendFaceRegions(r);
            Dispatcher.UIThread.Post(() => Faces.ShowResult(r));
        };
        Faces.WhenAnyValue(f => f.IsActive).Subscribe(_ => this.RaisePropertyChanged(nameof(ShowFaces)));
        Faces.ToggleCommand.ThrownExceptions.Subscribe(ex => Trace.TraceWarning($"Face toggle: {ex.Message}"));
        Faces.HideEveryoneCommand.ThrownExceptions.Subscribe(ex => Trace.TraceWarning($"Hide everyone: {ex.Message}"));

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
            // The address stays off the screen: it would show in screen shares and streams.
            DeviceSubtitle = Loc.DeviceSubtitle;
            // Frames decoded before this connection belong to the previous one.
            _lastPreviewFrame = _pipeline.PreviewInfo().Frame;
            IsConnected = true;
            _previewTimer!.Start();
            _vision.ResetTracks();
            _hands.ResetTracks();
            _faces.ResetTracks();
            ApplyVision();
            ApplyHands();
            ApplyFaces();
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
            Processing.ClearStats();
            ApplyVision();
            ApplyHands();
            ApplyFaces();
            _cameraOverlay.Clear();
            Interlocked.Exchange(ref _streamSize, 0);
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
            // Another lens, quality or orientation: tracks from the old picture mean nothing.
            Interlocked.Exchange(ref _streamSize, ((long)c.Width << 32) | (uint)c.Height);
            _vision.ResetTracks();
            _hands.ResetTracks();
            _faces.ResetTracks();
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

        // An unobserved command error would crash the app; show it where the user clicked instead.
        DisconnectCommand.ThrownExceptions.Subscribe(ex => StatusText = Loc.ActionFailed(ex.Message));
        CancelPairingCommand.ThrownExceptions.Subscribe(ex => StatusText = Loc.ActionFailed(ex.Message));
        InstallCameraCommand.ThrownExceptions.Subscribe(ex =>
            SetCameraStatus(false, Loc.CameraInstallFailedTitle, Loc.CameraInstallError(ex.Message), Loc.InstallCamera));
        ToggleFullScreenCommand.ThrownExceptions.Subscribe(ex => Trace.TraceWarning($"Full screen: {ex.Message}"));
        Processing.ResetColorCommand.ThrownExceptions.Subscribe(ex => Trace.TraceWarning($"Reset colour: {ex.Message}"));

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

    /// <summary>
    /// Master switch of the experimental features: NVIDIA noise removal, object analysis, hands. Off, none of them
    /// runs; their own switches keep their positions, so switching back on restores what was on. Saved.
    /// </summary>
    public bool ExperimentsEnabled
    {
        get => _settings.Experiments;
        set
        {
            if (value == _settings.Experiments)
                return;
            _settings = _settings with { Experiments = value };
            _settings.Save();
            this.RaisePropertyChanged();
            ApplyProcessing(Processing.EffectiveNative());
            ApplyVision();
            ApplyHands();
            ApplyFaces();
        }
    }

    /// <summary>Picture processing for the decoder; NVIDIA artifact removal only while the experiments are on.</summary>
    private void ApplyProcessing(HitCamProcessing settings)
    {
        if (!_settings.Experiments)
            settings.ArtifactReduction = ProcessingSettings.ArtifactReductionOff;
        _pipeline.SetProcessing(settings);
    }

    // Which features on the experiments tab show their settings; remembered.

    public bool ArtifactSectionExpanded { get => _settings.Sections.Artifact; set => SetSections(_settings.Sections with { Artifact = value }); }

    public bool VisionSectionExpanded { get => _settings.Sections.Vision; set => SetSections(_settings.Sections with { Vision = value }); }

    public bool HandsSectionExpanded { get => _settings.Sections.Hands; set => SetSections(_settings.Sections with { Hands = value }); }

    public bool FacesSectionExpanded { get => _settings.Sections.Faces; set => SetSections(_settings.Sections with { Faces = value }); }

    public bool EnhanceSectionExpanded { get => _settings.Sections.Enhance; set => SetSections(_settings.Sections with { Enhance = value }); }

    private void SetSections(ExperimentSections sections)
    {
        if (sections == _settings.Sections)
            return;
        _settings = _settings with { Sections = sections };
        _settings.Save();
        this.RaisePropertyChanged(nameof(ArtifactSectionExpanded));
        this.RaisePropertyChanged(nameof(VisionSectionExpanded));
        this.RaisePropertyChanged(nameof(HandsSectionExpanded));
        this.RaisePropertyChanged(nameof(FacesSectionExpanded));
        this.RaisePropertyChanged(nameof(EnhanceSectionExpanded));
    }

    /// <summary>Picture processing on this PC (noise reduction, colour, sharpness).</summary>
    public ProcessingViewModel Processing { get; }

    /// <summary>Object analysis: settings panel, stats and the tracks for the preview overlay.</summary>
    public VisionViewModel Vision { get; }

    /// <summary>Boxes are drawn over the preview: analysis runs and there is a picture.</summary>
    public bool ShowDetections => Vision.IsActive && HasPreview;

    /// <summary>Hand tracking: settings panel, stats and the hands for the preview overlay.</summary>
    public HandsViewModel Hands { get; }

    /// <summary>Hands are drawn over the preview: tracking runs and there is a picture.</summary>
    public bool ShowHands => Hands.IsActive && HasPreview;

    /// <summary>Points on the hands are drawn: tracking runs, there is a picture and they are not hidden.</summary>
    public bool ShowHandPoints => ShowHands && Hands.ShowPoints;

    /// <summary>Face hiding: settings panel, stats and the faces for the preview overlay.</summary>
    public FacesViewModel Faces { get; }

    /// <summary>Squares around faces are drawn over the preview: hiding runs and there is a picture.</summary>
    public bool ShowFaces => Faces.IsActive && HasPreview;

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
            this.RaisePropertyChanged(nameof(ShowDetections));
            this.RaisePropertyChanged(nameof(ShowHands));
            this.RaisePropertyChanged(nameof(ShowHandPoints));
            this.RaisePropertyChanged(nameof(ShowFaces));
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
        // Loading the NVIDIA libraries takes a moment; the artifact removal choice appears once they are found.
        _ = Task.Run(VideoPipeline.IsArtifactReductionAvailable).ContinueWith(
            t => Dispatcher.UIThread.Post(() => Processing.IsArtifactReductionAvailable = t.Result),
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
        {
            // A missing file or an older Windows is not fixed by reinstalling the camera.
            var explanation = NativeDiagnostics.ExplainCameraFailure(hr);
            SetCameraStatus(false, Loc.CameraFailedTitle, explanation ?? Loc.CameraFailedDetail($"0x{hr:X8}"), explanation is null ? Loc.ReinstallCamera : null);
        }
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

    /// <summary>
    /// Analysis runs while it is on and a phone is connected; otherwise the model is unloaded and the boxes leave the
    /// preview and the camera picture. Called on every change of the panel and on connect and disconnect.
    /// </summary>
    private void ApplyVision()
    {
        var model = Vision.SelectedModel?.Model;
        var active = ExperimentsEnabled && Vision.IsEnabled && IsConnected && model is not null;
        if (model is not null)
            _vision.Options = Vision.Settings.ToOptions(model);
        Vision.IsActive = active;
        if (active)
            _vision.Start(model!);
        else
            _vision.Stop();
        _cameraOverlay.ShowLabels = Vision.ShowLabels;
        _cameraOverlay.SetEnabled(active && Vision.BurnIn);
    }

    /// <summary>
    /// The hand scene (points, threads, fills) for the camera picture, on the tracking thread. An empty scene is sent
    /// once, so switching elements off clears them; while there is nothing to draw, nothing more is sent.
    /// </summary>
    private void SendHandScene(HandResult result)
    {
        var settings = _handsToCamera;
        var scene = settings is null ? HandCameraScene.Empty : HandCameraScene.Build(result, settings);
        if (scene.IsEmpty && !_handSceneSent)
            return;
        _pipeline.SetHandScene(scene);
        _handSceneSent = !scene.IsEmpty;
    }

    /// <summary>Tracking runs while it is on, the models are there and a phone is connected; otherwise they are unloaded.</summary>
    private void ApplyHands()
    {
        var active = ExperimentsEnabled && Hands.IsEnabled && Hands.HasModels && IsConnected;
        Hands.IsActive = active;
        _shotsToCamera = active && Hands.ShotsEnabled && Hands.CameraShots;
        _handsToCamera = active ? Hands.Settings : null;
        // Threads switched off are untied, so switching them on again starts from none.
        if (_threadsWereOn && !Hands.ThreadsEnabled)
            _hands.ResetTracks();
        _threadsWereOn = Hands.ThreadsEnabled;
        if (!active)
            _pipeline.SetHandScene(HandCameraScene.Empty);
        if (active)
            _hands.Start();
        else
            _hands.Stop();
    }

    /// <summary>
    /// Faces for the camera picture, on the face thread: sent with every result while there is something to draw (the
    /// DLL drops regions not refreshed within a second), and empty once when there no longer is.
    /// </summary>
    private void SendFaceRegions(FaceResult result)
    {
        var settings = _facesToCamera;
        var regions = settings is null ? [] : FaceStyle.CameraRegions(result.Faces, settings);
        if (regions.Length == 0 && !_faceRegionsSent)
            return;
        _pipeline.SetFaceRegions(regions);
        _faceRegionsSent = regions.Length > 0;
    }

    /// <summary>Face hiding runs while it is on, the models are there and a phone is connected; otherwise they are unloaded.</summary>
    private void ApplyFaces()
    {
        var active = ExperimentsEnabled && Faces.IsEnabled && Faces.HasModels && IsConnected;
        Faces.IsActive = active;
        _facesToCamera = active ? Faces.Settings : null;
        if (!active)
            _pipeline.SetFaceRegions([]);
        if (active)
            _faces.Start();
        else
            _faces.Stop();
    }

    /// <summary>
    /// Height in pixels of the picture the camera sends, for the size of burnt-in labels: the phone's stream in the
    /// preview's orientation, or twice the preview (which is at most 960×540) before the stream is known.
    /// </summary>
    private int CameraFrameHeight(int previewWidth, int previewHeight)
    {
        var size = Interlocked.Read(ref _streamSize);
        var (width, height) = ((int)(size >> 32), (int)(size & 0xFFFFFFFF));
        if (width <= 0 || height <= 0)
            return previewHeight * 2;
        return previewHeight > previewWidth ? Math.Max(width, height) : Math.Min(width, height);
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
            HideFaces(buffer.Address, buffer.RowBytes, width, height);
        }

        _lastPreviewFrame = frame;
        _nextPreviewBuffer ^= 1;
        Preview = bitmap;
    }

    /// <summary>
    /// Covers the hidden faces in the preview bitmap. Analysis reads the decoder's own copy, so it still sees them.
    /// </summary>
    private unsafe void HideFaces(IntPtr pixels, int stride, int width, int height)
    {
        if (!Faces.IsActive || Faces.Faces.Count == 0)
            return;
        var settings = Faces.Settings;
        var picture = new Span<byte>((void*)pixels, stride * height);
        foreach (var face in Faces.Faces)
        {
            if (face.Hidden)
                FaceEffects.Apply(picture, width, height, stride, face.Box, settings.Effect, settings.Strength / 100f,
                    HandSettings.ParseColor(settings.FillColor));
        }
    }

    private void UpdateStats()
    {
        if (_pipeline.Error is { } error)
            SetCameraStatus(false, Loc.CameraFailedTitle, Loc.DecoderFailed(error), null);

        if (!IsConnected)
            return;

        Processing.ShowStats(_pipeline.ProcessingStats());

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
        Processing.SaveNow();
        Vision.SaveNow();
        Hands.SaveNow();
        Faces.SaveNow();
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
        // Analysis reads the decoder's preview: it goes first.
        _vision.Dispose();
        _hands.Dispose();
        _faces.Dispose();
        _cameraOverlay.Clear();
        _pipeline.Dispose();
        _cameraOverlay.Dispose();
        _camera.Dispose();
    }
}

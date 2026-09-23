using System.Net.Sockets;
using System.Reactive;
using Avalonia.Media.Imaging;
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
    private long _framesInWindow;
    private long _bytesInWindow;
    private long _lastLatencyMicros = -1;

    private string _statusText = "";
    private string? _pin;
    private string _pinDevice = "";
    private bool _isConnected;
    private Bitmap? _qrCode;
    private string _addresses = "";
    private string _streamText = "—";
    private string _receivedText = "—";
    private string _latencyText = "—";
    private string _phoneText = "—";

    public MainViewModel()
    {
        _server = new HitCamServer(
            new HitCamServerOptions { Port = _settings.Port, ServerId = _settings.ServerId },
            new FilePairingStore(AppPaths.PairedDevices));

        _server.PairingStarted += p => Dispatcher.UIThread.Post(() =>
        {
            PinDevice = Loc.PairingFrom(p.DeviceName);
            Pin = p.Pin;
        });
        _server.PairingEnded += () => Dispatcher.UIThread.Post(() => Pin = null);
        _server.Connected += d => Dispatcher.UIThread.Post(() =>
        {
            IsConnected = true;
            StatusText = Loc.Connected(d.DeviceName);
        });
        _server.Disconnected += (_, reason) => Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            StatusText = Loc.Disconnected(reason);
            StreamText = ReceivedText = LatencyText = PhoneText = "—";
        });
        _server.StreamConfigReceived += c => Dispatcher.UIThread.Post(() =>
            StreamText = $"{c.Codec.ToUpperInvariant()} {c.Width}×{c.Height} @ {c.Fps} fps, {c.BitrateKbps / 1000.0:0.#} Mbit/s");
        _server.StatusReceived += s => Dispatcher.UIThread.Post(() =>
            PhoneText = $"{s.Battery:P0}{(s.Charging ? $" ({Loc.Charging})" : "")}, {s.Thermal}");
        _server.FrameReceived += f =>
        {
            Interlocked.Increment(ref _framesInWindow);
            Interlocked.Add(ref _bytesInWindow, f.Data.Length);
            if (f.LatencyMicros is { } latency)
                Interlocked.Exchange(ref _lastLatencyMicros, latency);
        };

        DisconnectCommand = ReactiveCommand.Create(() => _server.Kick());
        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStats());
    }

    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }

    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    /// <summary>PIN to show while a phone is pairing; null otherwise.</summary>
    public string? Pin
    {
        get => _pin;
        private set
        {
            this.RaiseAndSetIfChanged(ref _pin, value);
            this.RaisePropertyChanged(nameof(IsPairing));
        }
    }

    public bool IsPairing => Pin is not null;

    public string PinDevice { get => _pinDevice; private set => this.RaiseAndSetIfChanged(ref _pinDevice, value); }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isConnected, value);
            this.RaisePropertyChanged(nameof(IsWaiting));
        }
    }

    public bool IsWaiting => !IsConnected;

    public Bitmap? QrCode { get => _qrCode; private set => this.RaiseAndSetIfChanged(ref _qrCode, value); }

    public string Addresses { get => _addresses; private set => this.RaiseAndSetIfChanged(ref _addresses, value); }

    public string StreamText { get => _streamText; private set => this.RaiseAndSetIfChanged(ref _streamText, value); }

    public string ReceivedText { get => _receivedText; private set => this.RaiseAndSetIfChanged(ref _receivedText, value); }

    public string LatencyText { get => _latencyText; private set => this.RaiseAndSetIfChanged(ref _latencyText, value); }

    public string PhoneText { get => _phoneText; private set => this.RaiseAndSetIfChanged(ref _phoneText, value); }

    public void Start()
    {
        try
        {
            _server.Start();
        }
        catch (SocketException ex)
        {
            StatusText = Loc.ServerFailed(ex.Message);
            return;
        }

        RefreshAddresses();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) =>
            Dispatcher.UIThread.Post(RefreshAddresses);
        _statsTimer.Start();
    }

    private void RefreshAddresses()
    {
        var addresses = LanAddresses.Get();
        if (addresses.Count == 0)
        {
            Addresses = Loc.NoNetwork;
            QrCode = null;
            return;
        }

        Addresses = string.Join(Environment.NewLine, addresses.Select(a => $"{a}:{_server.Port}"));
        var name = Uri.EscapeDataString(Environment.MachineName);
        QrCode = QrImage.Create($"{ProtocolInfo.UriScheme}://{addresses[0]}:{_server.Port}?id={_settings.ServerId}&name={name}");
    }

    private void UpdateStats()
    {
        if (!IsConnected)
            return;

        var frames = Interlocked.Exchange(ref _framesInWindow, 0);
        var bytes = Interlocked.Exchange(ref _bytesInWindow, 0);
        ReceivedText = $"{frames} fps, {bytes * 8 / 1_000_000.0:0.0} Mbit/s";

        var latency = Interlocked.Read(ref _lastLatencyMicros);
        var rtt = _server.RoundTripMicros;
        LatencyText = (latency >= 0 ? $"{latency / 1000.0:0} ms" : "—")
                      + (rtt is { } r ? $" (RTT {r / 1000.0:0} ms)" : "");
    }

    public async ValueTask DisposeAsync()
    {
        _statsTimer.Stop();
        await _server.DisposeAsync();
    }
}

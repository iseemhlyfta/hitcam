using System.Net;
using System.Net.Sockets;
using HitCam.Core.Pairing;
using HitCam.Core.Protocol;

namespace HitCam.Core.Server;

public sealed record HitCamServerOptions
{
    public int Port { get; init; } = ProtocolInfo.DefaultPort;
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public string ServerName { get; init; } = Environment.MachineName;
    public string ServerId { get; init; } = Guid.NewGuid().ToString();
    /// <summary>Time allowed for the first Hello after connecting.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Time the user has to type the PIN on the phone.</summary>
    public TimeSpan PairingTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>How long a reconnecting phone waits for its previous, stale session to shut down.</summary>
    public TimeSpan TakeoverTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed record ConnectedDevice(string DeviceId, string DeviceName, string? Model, IPEndPoint RemoteEndPoint);

public sealed record PairingPrompt(string DeviceName, IPEndPoint RemoteEndPoint, string Pin);

/// <param name="Data">Annex-B access unit.</param>
/// <param name="PhoneTimestamp">Capture time on the phone clock, microseconds.</param>
/// <param name="LatencyMicros">Capture-to-receive latency, once the clocks are synchronized.</param>
public sealed record VideoFrame(byte[] Data, bool Keyframe, ulong PhoneTimestamp, long? LatencyMicros);

/// <summary>
/// Accepts phones over TCP, handles the handshake and pairing, and exposes the stream as events.
/// Only one phone streams at a time; a paired phone that reconnects replaces its own stale session.
/// Events are raised on background threads.
/// </summary>
public sealed class HitCamServer : IAsyncDisposable
{
    private readonly HitCamServerOptions _options;
    private readonly IPairingStore _pairingStore;
    private readonly PinGuard _pins;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly HashSet<Task> _clients = [];
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Connection? _owner;

    public HitCamServer(HitCamServerOptions options, IPairingStore pairingStore, TimeProvider? timeProvider = null)
    {
        _options = options;
        _pairingStore = pairingStore;
        _time = timeProvider ?? TimeProvider.System;
        _pins = new PinGuard(_time);
    }

    public event Action<PairingPrompt>? PairingStarted;
    public event Action? PairingEnded;
    public event Action<ConnectedDevice>? Connected;
    public event Action<ConnectedDevice, string>? Disconnected;
    public event Action<StreamConfig>? StreamConfigReceived;
    public event Action<VideoFrame>? FrameReceived;
    public event Action<Capabilities>? CapabilitiesReceived;
    public event Action<CameraState>? CameraStateReceived;
    public event Action<Status>? StatusReceived;

    /// <summary>The port actually listened on (useful when Port = 0).</summary>
    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    public ConnectedDevice? CurrentDevice => Volatile.Read(ref _owner)?.Device;

    public long? RoundTripMicros => Volatile.Read(ref _owner)?.Clock.RoundTripMicros;

    public void Start()
    {
        if (_listener is not null)
            throw new InvalidOperationException("Server is already running.");

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(_options.BindAddress, _options.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_listener, _cts.Token);
    }

    public async Task StopAsync()
    {
        if (_listener is null)
            return;

        await _cts!.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _acceptLoop!.ConfigureAwait(false);

        Task[] clients;
        lock (_gate)
            clients = [.. _clients];
        await Task.WhenAll(clients).ConfigureAwait(false);

        _cts.Dispose();
        _cts = null;
        _listener = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public Task<bool> SendControlAsync(Control control, CancellationToken cancellationToken = default) =>
        SendAsync(Message.Json(MessageType.Control, control, ProtocolJson.Default.Control, Now()), cancellationToken);

    public Task<bool> RequestKeyframeAsync(CancellationToken cancellationToken = default) =>
        SendAsync(Message.Empty(MessageType.RequestKeyframe, Now()), cancellationToken);

    /// <summary>Disconnects the current phone (or aborts a pairing in progress).</summary>
    public void Kick() => Volatile.Read(ref _owner)?.Cancel("disconnected on PC");

    private async Task<bool> SendAsync(Message message, CancellationToken cancellationToken)
    {
        var owner = Volatile.Read(ref _owner);
        if (owner?.Device is null)
            return false;
        try
        {
            await owner.Stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var task = HandleClientAsync(client, cancellationToken);
            lock (_gate)
                _clients.Add(task);
            _ = task.ContinueWith(t =>
            {
                lock (_gate)
                    _clients.Remove(t);
            }, TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using var tcp = client;
        client.NoDelay = true;
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        await using var stream = new MessageStream(client.GetStream());
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var token = connectionCts.Token;
        Connection? connection = null;
        try
        {
            var hello = await ReadHelloAsync(stream, token).ConfigureAwait(false);
            if (hello is null)
                return;

            if (hello.ProtocolVersion != ProtocolInfo.Version)
            {
                await SendAckAsync(stream, HelloStatus.VersionMismatch, token).ConfigureAwait(false);
                return;
            }

            var paired = _pairingStore.Verify(hello.DeviceId, hello.Token);
            connection = new Connection(hello.DeviceId, stream, connectionCts);
            if (!await ClaimAsync(connection, takeover: paired, token).ConfigureAwait(false))
            {
                connection = null;
                await SendAckAsync(stream, HelloStatus.Busy, token).ConfigureAwait(false);
                return;
            }

            if (paired)
            {
                _pairingStore.Touch(hello.DeviceId);
                await SendAckAsync(stream, HelloStatus.Accepted, token).ConfigureAwait(false);
            }
            else if (!await PairAsync(stream, hello, remote, token).ConfigureAwait(false))
            {
                return;
            }

            var device = new ConnectedDevice(hello.DeviceId, hello.DeviceName, hello.Model, remote);
            await RunSessionAsync(connection, device, serverToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ProtocolException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            // Dropped during the handshake; RunSessionAsync reports disconnects of established sessions.
        }
        finally
        {
            if (connection is not null)
                Release(connection);
        }
    }

    /// <summary>
    /// Takes the single streaming slot. A paired phone may evict a session of the same device,
    /// which is usually a half-open connection left over from a Wi-Fi drop.
    /// </summary>
    private async Task<bool> ClaimAsync(Connection connection, bool takeover, CancellationToken cancellationToken)
    {
        Connection? stale;
        lock (_gate)
        {
            if (_owner is null)
            {
                _owner = connection;
                return true;
            }
            stale = _owner;
        }

        if (!takeover || stale.DeviceId != connection.DeviceId)
            return false;

        stale.Cancel("replaced by a new connection");
        var deadline = _time.GetTimestamp() + (long)(_options.TakeoverTimeout.TotalSeconds * _time.TimestampFrequency);
        while (_time.GetTimestamp() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), _time, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_owner is null)
                {
                    _owner = connection;
                    return true;
                }
            }
        }
        return false;
    }

    private void Release(Connection connection)
    {
        lock (_gate)
        {
            if (_owner == connection)
                _owner = null;
        }
    }

    private async Task<Hello?> ReadHelloAsync(MessageStream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HelloTimeout);
        var message = await stream.ReadAsync(timeout.Token).ConfigureAwait(false);
        if (message is null || message.Type != MessageType.Hello)
            return null;

        var hello = message.ReadJson(ProtocolJson.Default.Hello);
        if (string.IsNullOrWhiteSpace(hello.DeviceId) || hello.DeviceId.Length > 64)
            throw new ProtocolException("Hello.deviceId is missing or too long.");
        var name = string.IsNullOrWhiteSpace(hello.DeviceName) ? "iPhone" : hello.DeviceName.Trim();
        return hello with { DeviceName = name.Length > 64 ? name[..64] : name };
    }

    private async Task<bool> PairAsync(MessageStream stream, Hello hello, IPEndPoint remote, CancellationToken cancellationToken)
    {
        var pin = _pins.Begin();
        if (pin is null)
        {
            await SendAckAsync(stream, HelloStatus.PairingLocked, cancellationToken).ConfigureAwait(false);
            return false;
        }

        await SendAckAsync(stream, HelloStatus.PairingRequired, cancellationToken).ConfigureAwait(false);
        PairingStarted?.Invoke(new PairingPrompt(hello.DeviceName, remote, pin));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.PairingTimeout);
            while (true)
            {
                var message = await stream.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (message is null)
                    return false;
                if (message.Type != MessageType.PairRequest)
                    throw new ProtocolException($"Expected PairRequest, got {message.Type}.");

                var request = message.ReadJson(ProtocolJson.Default.PairRequest);
                switch (_pins.Check(request.Pin))
                {
                    case PinCheckResult.Ok:
                        var token = _pairingStore.Pair(hello.DeviceId, hello.DeviceName);
                        await SendPairResultAsync(stream, new PairResult(true, token, _pins.AttemptsLeft), timeout.Token).ConfigureAwait(false);
                        return true;
                    case PinCheckResult.Wrong:
                        await SendPairResultAsync(stream, new PairResult(false, null, _pins.AttemptsLeft), timeout.Token).ConfigureAwait(false);
                        break;
                    default:
                        await SendPairResultAsync(stream, new PairResult(false, null, 0), timeout.Token).ConfigureAwait(false);
                        return false;
                }
            }
        }
        finally
        {
            _pins.Cancel();
            PairingEnded?.Invoke();
        }
    }

    private async Task RunSessionAsync(Connection connection, ConnectedDevice device, CancellationToken serverToken)
    {
        var token = connection.Token;
        connection.Device = device;
        Connected?.Invoke(device);

        string reason;
        var pingLoop = PingLoopAsync(connection, token);
        try
        {
            reason = await ReceiveLoopAsync(connection, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            reason = serverToken.IsCancellationRequested ? "server stopped" : connection.CancelReason ?? "timed out";
        }
        catch (Exception ex) when (ex is IOException or SocketException or ProtocolException or ObjectDisposedException)
        {
            reason = connection.CancelReason ?? ex.Message;
        }
        finally
        {
            connection.Cancel("session ended");
        }

        await pingLoop.ConfigureAwait(false);
        Disconnected?.Invoke(device, reason);
    }

    private async Task<string> ReceiveLoopAsync(Connection connection, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            idle.CancelAfter(_options.IdleTimeout);
            var message = await connection.Stream.ReadAsync(idle.Token).ConfigureAwait(false);
            if (message is null)
                return "closed by phone";

            switch (message.Type)
            {
                case MessageType.VideoFrame:
                    var local = connection.Clock.ToLocal(message.Header.Timestamp);
                    long? latency = local is { } captured ? (long)Now() - (long)captured : null;
                    FrameReceived?.Invoke(new VideoFrame(message.Payload, message.IsKeyframe, message.Header.Timestamp, latency));
                    break;
                case MessageType.StreamConfig:
                    StreamConfigReceived?.Invoke(message.ReadJson(ProtocolJson.Default.StreamConfig));
                    break;
                case MessageType.Capabilities:
                    CapabilitiesReceived?.Invoke(message.ReadJson(ProtocolJson.Default.Capabilities));
                    break;
                case MessageType.CameraState:
                    CameraStateReceived?.Invoke(message.ReadJson(ProtocolJson.Default.CameraState));
                    break;
                case MessageType.Status:
                    StatusReceived?.Invoke(message.ReadJson(ProtocolJson.Default.Status));
                    break;
                case MessageType.Ping:
                    await connection.Stream.WriteAsync(Message.Pong(message.Header.Timestamp, Now()), cancellationToken).ConfigureAwait(false);
                    break;
                case MessageType.Pong:
                    connection.Clock.AddSample(message.ReadPongTimestamp(), message.Header.Timestamp, Now());
                    break;
                case MessageType.Bye:
                    return "closed by phone";
                default:
                    // Unknown or unexpected types are skipped for forward compatibility.
                    break;
            }
        }
    }

    private async Task PingLoopAsync(Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.PingInterval, _time);
            do
            {
                await connection.Stream.WriteAsync(Message.Empty(MessageType.Ping, Now()), cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            connection.Cancel("connection lost");
        }
    }

    private Task SendAckAsync(MessageStream stream, string status, CancellationToken cancellationToken) =>
        stream.WriteAsync(Message.Json(
            MessageType.HelloAck,
            new HelloAck(ProtocolInfo.Version, status, _options.ServerName, _options.ServerId),
            ProtocolJson.Default.HelloAck,
            Now()), cancellationToken).AsTask();

    private Task SendPairResultAsync(MessageStream stream, PairResult result, CancellationToken cancellationToken) =>
        stream.WriteAsync(Message.Json(MessageType.PairResult, result, ProtocolJson.Default.PairResult, Now()), cancellationToken).AsTask();

    /// <summary>Monotonic PC clock in microseconds.</summary>
    private ulong Now() => (ulong)(_time.GetTimestamp() * 1_000_000.0 / _time.TimestampFrequency);

    /// <summary>A phone holding the streaming slot, from Hello until disconnect.</summary>
    private sealed class Connection(string deviceId, MessageStream stream, CancellationTokenSource cts)
    {
        private string? _cancelReason;

        public string DeviceId { get; } = deviceId;
        public MessageStream Stream { get; } = stream;
        public ClockSync Clock { get; } = new();
        public CancellationToken Token { get; } = cts.Token;
        /// <summary>Set once the handshake has completed.</summary>
        public ConnectedDevice? Device { get; set; }
        public string? CancelReason => Volatile.Read(ref _cancelReason);

        public void Cancel(string reason)
        {
            Interlocked.CompareExchange(ref _cancelReason, reason, null);
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

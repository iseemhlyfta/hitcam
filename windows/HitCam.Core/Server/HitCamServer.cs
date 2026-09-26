using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using HitCam.Core.Pairing;
using HitCam.Core.Protocol;
using HitCam.Core.Security;

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
    /// <summary>Open connections at most; more are closed right away.</summary>
    public int MaxConnections { get; init; } = 8;
    /// <summary>Unfinished handshakes (including pairing) per remote address; more are closed right away.</summary>
    public int MaxHandshakesPerAddress { get; init; } = 2;
    /// <summary>TLS identity for protocol v2; null: plain v1 only.</summary>
    public ServerIdentity? Identity { get; init; }
    /// <summary>Accept phone apps up to 0.3.0 over plain TCP (protocol v1).</summary>
    public bool AllowPlaintext { get; init; } = true;
}

/// <param name="Encrypted">Over TLS (protocol v2); false for an old phone app on plain TCP.</param>
public sealed record ConnectedDevice(string DeviceId, string DeviceName, string? Model, IPEndPoint RemoteEndPoint, bool Encrypted = false);

public sealed record PairingPrompt(string DeviceName, IPEndPoint RemoteEndPoint, string Pin);

/// <param name="Data">Annex-B access unit.</param>
/// <param name="PhoneTimestamp">Capture time on the phone clock, microseconds.</param>
/// <param name="LatencyMicros">Capture-to-receive latency, once the clocks are synchronized.</param>
public sealed record VideoFrame(byte[] Data, bool Keyframe, ulong PhoneTimestamp, long? LatencyMicros);

/// <summary>
/// Accepts phones over TCP, handles the handshake and pairing, and exposes the stream as events.
/// Only one phone streams (or pairs) at a time; a paired phone replaces its own stale session and
/// any pairing in progress. Events are raised on background threads; exceptions thrown by handlers
/// are traced and do not affect the session.
/// </summary>
public sealed class HitCamServer : IAsyncDisposable
{
    private static readonly Func<MessageType, int> HandshakeLimit = static _ => PayloadLimits.Handshake;
    private static readonly Func<MessageType, int> SessionLimit = PayloadLimits.ForSession;

    private readonly HitCamServerOptions _options;
    private readonly IPairingStore _pairingStore;
    private readonly PinGuard _pins;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly HashSet<Task> _clients = [];
    private readonly Dictionary<IPAddress, int> _handshakes = [];
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Connection? _owner;
    private bool _allowPlaintext;

    public HitCamServer(HitCamServerOptions options, IPairingStore pairingStore, TimeProvider? timeProvider = null)
    {
        _options = options;
        _pairingStore = pairingStore;
        _time = timeProvider ?? TimeProvider.System;
        _pins = new PinGuard(_time);
        _allowPlaintext = options.AllowPlaintext;
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

    /// <summary>Accept old phone apps over plain TCP (v1); starts as <see cref="HitCamServerOptions.AllowPlaintext"/>.</summary>
    public bool AllowPlaintext
    {
        get => Volatile.Read(ref _allowPlaintext);
        set
        {
            Volatile.Write(ref _allowPlaintext, value);
            // Off: a plain phone on now (streaming or still pairing) goes too.
            if (!value && Volatile.Read(ref _owner) is { Encrypted: false } owner)
                _ = SayByeAndCloseAsync(owner, "unencrypted phones turned off");
        }
    }

    public ConnectedDevice? CurrentDevice => Volatile.Read(ref _owner)?.Device;

    public long? RoundTripMicros => Volatile.Read(ref _owner)?.Clock.RoundTripMicros;

    public void Start()
    {
        if (_listener is not null)
            throw new InvalidOperationException("Server is already running.");

        // Fields only once listening: a port in use leaves the server stopped (and disposable), not half started.
        var listener = new TcpListener(_options.BindAddress, _options.Port);
        try
        {
            listener.Start();
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        _cts = new CancellationTokenSource();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(listener, _cts.Token);
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

    /// <summary>
    /// Disconnects the phone (the PC's "Disconnect" button). A Bye goes first: without it the phone takes the closed
    /// connection for a network drop and reconnects two seconds later.
    /// </summary>
    public void Kick()
    {
        if (Volatile.Read(ref _owner) is { } owner)
            _ = SayByeAndCloseAsync(owner, "disconnected on PC");
    }

    private static async Task SayByeAndCloseAsync(Connection owner, string reason)
    {
        try
        {
            // Briefly: a stalled connection must not keep the phone on the screen.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await owner.Stream.WriteAsync(Message.Json(MessageType.Bye, new Bye(reason), ProtocolJson.Default.Bye), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
        {
        }
        owner.Cancel(reason);
    }

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
            IPEndPoint remote;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            try
            {
                remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                client.Dispose();
                continue;
            }

            // Anyone on the network can connect: cap what they can hold open before proving anything.
            var address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
            bool admitted;
            lock (_gate)
            {
                var pending = _handshakes.GetValueOrDefault(address);
                admitted = _clients.Count < _options.MaxConnections && pending < _options.MaxHandshakesPerAddress;
                if (admitted)
                    _handshakes[address] = pending + 1;
            }
            if (!admitted)
            {
                client.Dispose();
                continue;
            }

            var task = HandleClientAsync(client, remote, address, cancellationToken);
            lock (_gate)
                _clients.Add(task);
            _ = task.ContinueWith(t =>
            {
                lock (_gate)
                    _clients.Remove(t);
            }, TaskScheduler.Default);
        }
    }

    private void EndHandshake(IPAddress address)
    {
        lock (_gate)
        {
            if (_handshakes.TryGetValue(address, out var pending) && pending > 1)
                _handshakes[address] = pending - 1;
            else
                _handshakes.Remove(address);
        }
    }

    private async Task HandleClientAsync(TcpClient client, IPEndPoint remote, IPAddress address, CancellationToken serverToken)
    {
        var handshaking = true;
        Connection? connection = null;
        try
        {
            using var tcp = client;
            client.NoDelay = true;
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            var token = connectionCts.Token;
            var transport = await OpenTransportAsync(client, token).ConfigureAwait(false);
            if (transport is null)
                return;
            var encrypted = transport is SslStream;
            await using var stream = new MessageStream(transport);
            try
            {
                var hello = await ReadHelloAsync(stream, token).ConfigureAwait(false);
                if (hello is null)
                    return;

                // v2 only over TLS, v1 only in plain TCP and only if allowed: no downgrade either way.
                var version = encrypted ? ProtocolInfo.Version : ProtocolInfo.LegacyVersion;
                if (hello.ProtocolVersion != version || (!encrypted && !AllowPlaintext && _options.Identity is not null))
                {
                    await SendAckAsync(stream, version, HelloStatus.VersionMismatch, token).ConfigureAwait(false);
                    return;
                }

                // Over TLS only a token issued over TLS: one from plain TCP may have been seen on the network.
                var paired = _pairingStore.Verify(hello.DeviceId, hello.Token, encryptedOnly: encrypted);
                connection = new Connection(hello.DeviceId, stream, connectionCts) { Pairing = !paired, Encrypted = encrypted };
                if (!await ClaimAsync(connection, paired, token).ConfigureAwait(false))
                {
                    connection = null;
                    await SendAckAsync(stream, version, HelloStatus.Busy, token).ConfigureAwait(false);
                    return;
                }
                // Plain phones may have been turned off while the claim waited for a stale session.
                if (!encrypted && !AllowPlaintext && _options.Identity is not null)
                {
                    await SendAckAsync(stream, version, HelloStatus.VersionMismatch, token).ConfigureAwait(false);
                    return;
                }

                if (paired)
                {
                    _pairingStore.Touch(hello.DeviceId);
                    await SendAckAsync(stream, version, HelloStatus.Accepted, token).ConfigureAwait(false);
                }
                else if (!await (encrypted
                             ? PairOverTlsAsync(connection, hello, remote, token)
                             : PairAsync(connection, hello, remote, token)).ConfigureAwait(false))
                {
                    return;
                }

                handshaking = false;
                EndHandshake(address);
                var device = new ConnectedDevice(hello.DeviceId, hello.DeviceName, hello.Model, remote, encrypted);
                await RunSessionAsync(connection, device, serverToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ProtocolException
                                           or OperationCanceledException or ObjectDisposedException)
            {
                // Dropped during the handshake; RunSessionAsync reports disconnects of established sessions.
            }
            finally
            {
                // Before the socket closes, so a phone that reconnects right away finds the slots free.
                if (connection is not null)
                    Release(connection);
                if (handshaking)
                {
                    handshaking = false;
                    EndHandshake(address);
                }
            }
        }
        catch (Exception ex)
        {
            // Anything else (e.g. a failing pairing store) ends only this connection; StopAsync must not see it.
            Trace.TraceError($"HitCam: connection from {remote} failed: {ex}");
        }
        finally
        {
            if (handshaking)
                EndHandshake(address);
        }
    }

    /// <summary>
    /// TLS if the phone starts with a TLS record (0x16, a ClientHello) and the server has an identity, otherwise the
    /// plain stream (a v1 Hello starts with 0x01). Null if the phone left or TLS failed within the Hello timeout.
    /// </summary>
    private async Task<Stream?> OpenTransportAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HelloTimeout);
        SslStream? tls = null;
        try
        {
            var first = new byte[1];
            if (await client.Client.ReceiveAsync(first, SocketFlags.Peek, timeout.Token).ConfigureAwait(false) == 0)
                return null;
            if (first[0] != 0x16 || _options.Identity is not { } identity)
                return client.GetStream();

            tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity.Certificate,
                // 1.2 as well: Windows 10 cannot serve TLS 1.3.
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
            }, timeout.Token).ConfigureAwait(false);
            return tls;
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            if (tls is not null)
                await tls.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Takes the single streaming slot. A paired phone may evict a pairing in progress (so a stranger typing
    /// PINs cannot lock it out) or a session of the same device, which is usually a half-open connection
    /// left over from a Wi-Fi drop.
    /// </summary>
    private async Task<bool> ClaimAsync(Connection connection, bool paired, CancellationToken cancellationToken)
    {
        Connection stale;
        string reason;
        lock (_gate)
        {
            if (_owner is null)
            {
                _owner = connection;
                return true;
            }
            stale = _owner;
            if (!paired || !(stale.Pairing || stale.DeviceId == connection.DeviceId))
                return false;
            // Decided under the lock, so a pairing that completes concurrently sees it and gives up.
            reason = stale.Pairing ? "replaced by a paired phone" : "replaced by a new connection";
            stale.RequestCancel(reason);
        }

        stale.Cancel(reason);
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

    /// <summary>Marks a pairing as done, unless a paired phone has already evicted it.</summary>
    private bool TryFinishPairing(Connection connection)
    {
        lock (_gate)
        {
            if (connection.CancelReason is not null)
                return false;
            connection.Pairing = false;
            return true;
        }
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
        var message = await stream.ReadAsync(HandshakeLimit, timeout.Token).ConfigureAwait(false);
        if (message is null || message.Type != MessageType.Hello)
            return null;

        var hello = message.ReadJson(ProtocolJson.Default.Hello);
        if (string.IsNullOrWhiteSpace(hello.DeviceId) || hello.DeviceId.Length > 64)
            throw new ProtocolException("Hello.deviceId is missing or too long.");
        var name = string.IsNullOrWhiteSpace(hello.DeviceName) ? "Phone" : hello.DeviceName.Trim();
        return hello with { DeviceName = name.Length > 64 ? name[..64] : name };
    }

    private async Task<bool> PairAsync(Connection connection, Hello hello, IPEndPoint remote, CancellationToken cancellationToken)
    {
        var stream = connection.Stream;
        var pin = _pins.Begin();
        if (pin is null)
        {
            await SendAckAsync(stream, ProtocolInfo.LegacyVersion, HelloStatus.PairingLocked, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var started = false;
        var paired = false;
        try
        {
            await SendAckAsync(stream, ProtocolInfo.LegacyVersion, HelloStatus.PairingRequired, cancellationToken).ConfigureAwait(false);
            started = true;
            Raise(PairingStarted, new PairingPrompt(hello.DeviceName, remote, pin));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.PairingTimeout);
            while (true)
            {
                var message = await stream.ReadAsync(HandshakeLimit, timeout.Token).ConfigureAwait(false);
                if (message is null)
                    return false;
                if (message.Type != MessageType.PairRequest)
                    throw new ProtocolException($"Expected PairRequest, got {message.Type}.");

                var request = message.ReadJson(ProtocolJson.Default.PairRequest);
                switch (_pins.Check(request.Pin))
                {
                    case PinCheckResult.Ok:
                        if (!TryFinishPairing(connection))
                            return false;
                        var token = _pairingStore.Pair(hello.DeviceId, hello.DeviceName);
                        await SendPairResultAsync(stream, new PairResult(true, token, _pins.AttemptsLeft), timeout.Token).ConfigureAwait(false);
                        paired = true;
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
            // Closes the PIN window; wrong attempts made so far keep counting towards the lockout.
            _pins.Cancel();
            // Free the slot before announcing the end, so whoever reacts to it can connect.
            if (!paired)
                Release(connection);
            if (started)
                Raise(PairingEnded);
        }
    }

    /// <summary>
    /// Pairing over TLS (v2): the PIN shown on the PC is proven through commitments bound to the certificate (see
    /// <see cref="PinProof"/>), so a middleman with its own certificate can neither pass nor learn the PIN in time.
    /// One PIN per connection: once the PC has opened its commitment the PIN is used up, right or wrong.
    /// </summary>
    private async Task<bool> PairOverTlsAsync(Connection connection, Hello hello, IPEndPoint remote, CancellationToken cancellationToken)
    {
        var stream = connection.Stream;
        var fingerprint = _options.Identity!.Fingerprint;
        var pin = _pins.Begin();
        if (pin is null)
        {
            await SendAckAsync(stream, ProtocolInfo.Version, HelloStatus.PairingLocked, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var started = false;
        var paired = false;
        var revealed = false;
        var checkedPin = false;
        var nonce = PinProof.NewNonce();
        try
        {
            var commit = PinProof.Commit(PinProof.PcLabel, fingerprint, pin, nonce);
            await SendAckAsync(stream, ProtocolInfo.Version, HelloStatus.PairingRequired, cancellationToken, Convert.ToHexStringLower(commit))
                .ConfigureAwait(false);
            started = true;
            Raise(PairingStarted, new PairingPrompt(hello.DeviceName, remote, pin));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.PairingTimeout);
            var request = await ReadPairingAsync(stream, MessageType.PairRequest, timeout.Token).ConfigureAwait(false);
            var phoneCommit = PinProof.FromHex(request?.ReadJson(ProtocolJson.Default.PairRequest).Commit, 32);
            if (phoneCommit is null)
                return false;

            // The phone is bound now; open ours. From here on the PIN is used up.
            revealed = true;
            await stream.WriteAsync(Message.Json(MessageType.PairReveal, new PairNonce(Convert.ToHexStringLower(nonce)),
                ProtocolJson.Default.PairNonce, Now()), timeout.Token).ConfigureAwait(false);

            var confirm = await ReadPairingAsync(stream, MessageType.PairConfirm, timeout.Token).ConfigureAwait(false);
            var phoneNonce = PinProof.FromHex(confirm?.ReadJson(ProtocolJson.Default.PairNonce).Nonce, PinProof.NonceSize);
            if (phoneNonce is null)
                return false;

            checkedPin = true;
            var result = _pins.Check(candidate => CryptographicOperations.FixedTimeEquals(
                phoneCommit, PinProof.Commit(PinProof.PhoneLabel, fingerprint, candidate, phoneNonce)));
            if (result != PinCheckResult.Ok)
            {
                await SendPairResultAsync(stream, new PairResult(false, null, result == PinCheckResult.Wrong ? _pins.AttemptsLeft : 0),
                    timeout.Token).ConfigureAwait(false);
                return false;
            }
            if (!TryFinishPairing(connection))
                return false;
            var token = _pairingStore.Pair(hello.DeviceId, hello.DeviceName, encrypted: true);
            await SendPairResultAsync(stream, new PairResult(true, token, _pins.AttemptsLeft), timeout.Token).ConfigureAwait(false);
            paired = true;
            return true;
        }
        finally
        {
            // A phone that saw our nonce and left without confirming (a mistyped PIN, a middleman) used up a PIN: it
            // counts as a wrong one, or opening commitments would be free guesses.
            if (revealed && !checkedPin)
                _pins.Check(static _ => false);
            _pins.Cancel();
            if (!paired)
                Release(connection);
            if (started)
                Raise(PairingEnded);
        }
    }

    /// <summary>The next pairing message of the given type; null if the phone closed; anything else is an error.</summary>
    private static async Task<Message?> ReadPairingAsync(MessageStream stream, MessageType type, CancellationToken cancellationToken)
    {
        var message = await stream.ReadAsync(HandshakeLimit, cancellationToken).ConfigureAwait(false);
        if (message is not null && message.Type != type)
            throw new ProtocolException($"Expected {type}, got {message.Type}.");
        return message;
    }

    private async Task RunSessionAsync(Connection connection, ConnectedDevice device, CancellationToken serverToken)
    {
        var token = connection.Token;
        connection.Device = device;

        var reason = "session ended";
        var pingLoop = Task.CompletedTask;
        try
        {
            Raise(Connected, device);
            pingLoop = PingLoopAsync(connection, token);
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
            await pingLoop.ConfigureAwait(false);
            Raise(Disconnected, device, reason);
        }
    }

    private async Task<string> ReceiveLoopAsync(Connection connection, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            idle.CancelAfter(_options.IdleTimeout);
            var message = await connection.Stream.ReadAsync(SessionLimit, idle.Token).ConfigureAwait(false);
            if (message is null)
                return "closed by phone";

            switch (message.Type)
            {
                case MessageType.VideoFrame:
                    var local = connection.Clock.ToLocal(message.Header.Timestamp);
                    long? latency = local is { } captured ? (long)Now() - (long)captured : null;
                    Raise(FrameReceived, new VideoFrame(message.Payload, message.IsKeyframe, message.Header.Timestamp, latency));
                    break;
                case MessageType.StreamConfig:
                    Raise(StreamConfigReceived, message.ReadJson(ProtocolJson.Default.StreamConfig));
                    break;
                case MessageType.Capabilities:
                    Raise(CapabilitiesReceived, message.ReadJson(ProtocolJson.Default.Capabilities));
                    break;
                case MessageType.CameraState:
                    Raise(CameraStateReceived, message.ReadJson(ProtocolJson.Default.CameraState));
                    break;
                case MessageType.Status:
                    Raise(StatusReceived, message.ReadJson(ProtocolJson.Default.Status));
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

    // Handlers run synchronously on the connection's thread: one that throws must not end the session,
    // skip the other subscribers, or leave the PC showing a PIN / a phone that is gone.
    private static void Raise(Action? handler, [CallerArgumentExpression(nameof(handler))] string name = "")
    {
        foreach (var subscriber in Delegate.EnumerateInvocationList(handler))
        {
            try
            {
                subscriber();
            }
            catch (Exception ex)
            {
                TraceHandlerFailure(name, ex);
            }
        }
    }

    private static void Raise<T>(Action<T>? handler, T arg, [CallerArgumentExpression(nameof(handler))] string name = "")
    {
        foreach (var subscriber in Delegate.EnumerateInvocationList(handler))
        {
            try
            {
                subscriber(arg);
            }
            catch (Exception ex)
            {
                TraceHandlerFailure(name, ex);
            }
        }
    }

    private static void Raise<T1, T2>(Action<T1, T2>? handler, T1 arg1, T2 arg2, [CallerArgumentExpression(nameof(handler))] string name = "")
    {
        foreach (var subscriber in Delegate.EnumerateInvocationList(handler))
        {
            try
            {
                subscriber(arg1, arg2);
            }
            catch (Exception ex)
            {
                TraceHandlerFailure(name, ex);
            }
        }
    }

    private static void TraceHandlerFailure(string name, Exception ex) =>
        Trace.TraceError($"HitCam: {name} handler threw: {ex}");

    private Task SendAckAsync(MessageStream stream, int version, string status, CancellationToken cancellationToken, string? pinCommit = null) =>
        stream.WriteAsync(Message.Json(
            MessageType.HelloAck,
            new HelloAck(version, status, _options.ServerName, _options.ServerId, pinCommit),
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
        /// <summary>Over TLS (protocol v2).</summary>
        public bool Encrypted { get; init; }
        /// <summary>Still waiting for the PIN; a paired phone may evict it. Changed under the server lock.</summary>
        public bool Pairing { get; set; }
        /// <summary>Set once the handshake has completed.</summary>
        public ConnectedDevice? Device { get; set; }
        public string? CancelReason => Volatile.Read(ref _cancelReason);

        /// <summary>Records why the connection ends; the first reason wins.</summary>
        public void RequestCancel(string reason) => Interlocked.CompareExchange(ref _cancelReason, reason, null);

        public void Cancel(string reason)
        {
            RequestCancel(reason);
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

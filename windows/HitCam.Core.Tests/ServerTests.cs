using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using HitCam.Core.Pairing;
using HitCam.Core.Protocol;
using HitCam.Core.Server;

namespace HitCam.Core.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    private readonly InMemoryPairingStore _store = new();
    private HitCamServer _server = null!;
    private readonly List<TestPhone> _phones = [];
    private readonly List<HitCamServer> _servers = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _server = new HitCamServer(
            new HitCamServerOptions { Port = 0, BindAddress = IPAddress.Loopback, ServerName = "test-pc" },
            _store);
        _server.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var phone in _phones)
            await phone.DisposeAsync();
        await _server.DisposeAsync();
        foreach (var server in _servers)
            await server.DisposeAsync();
    }

    [Fact]
    public async Task New_phone_pairs_with_pin_shown_on_pc_then_reconnects_with_token()
    {
        var prompt = new TaskCompletionSource<PairingPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.PairingStarted += p => prompt.TrySetResult(p);

        var phone = await ConnectAsync();
        var ack = await phone.HelloAsync(token: null);
        Assert.Equal(HelloStatus.PairingRequired, ack.Status);
        Assert.Equal("test-pc", ack.ServerName);

        var pin = (await prompt.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Pin;
        var wrong = await phone.PairAsync(pin == "000000" ? "111111" : "000000");
        Assert.False(wrong.Ok);
        Assert.Equal(PinGuard.MaxAttempts - 1, wrong.AttemptsLeft);

        var result = await phone.PairAsync(pin);
        Assert.True(result.Ok);
        Assert.NotNull(result.Token);
        await phone.DisposeAsync();

        var again = await ConnectAsync();
        Assert.Equal(HelloStatus.Accepted, (await again.HelloAsync(result.Token)).Status);
    }

    [Fact]
    public async Task Frames_and_control_flow_in_both_directions()
    {
        var connected = new TaskCompletionSource<ConnectedDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Connected += d => connected.TrySetResult(d);
        _server.FrameReceived += f => frame.TrySetResult(f);

        var phone = await ConnectPairedAsync();
        var device = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("phone-1", device.DeviceId);

        await phone.Stream.WriteAsync(new Message(new MessageHeader(MessageType.VideoFrame, MessageFlags.Keyframe, 4, 777), [0, 0, 1, 0x65]), Ct);
        var received = await frame.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(received.Keyframe);
        Assert.Equal(777UL, received.PhoneTimestamp);

        Assert.True(await _server.SendControlAsync(new Control { Torch = true }, Ct));
        var control = await phone.ReadUntilAsync(MessageType.Control);
        Assert.True(control.ReadJson(ProtocolJson.Default.Control).Torch);
    }

    [Fact]
    public async Task Second_phone_is_told_the_pc_is_busy()
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Connected += _ => connected.TrySetResult();
        await ConnectPairedAsync();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var second = await ConnectAsync("phone-2");
        Assert.Equal(HelloStatus.Busy, (await second.HelloAsync(null)).Status);
    }

    [Fact]
    public async Task Reconnecting_phone_replaces_its_stale_session()
    {
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        var secondConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Disconnected += (_, reason) => disconnected.TrySetResult(reason);
        _server.Connected += _ =>
        {
            if (Interlocked.Increment(ref connections) == 2)
                secondConnected.TrySetResult();
        };

        var token = _store.Pair("phone-1", "iPhone");
        var stale = await ConnectAsync();
        Assert.Equal(HelloStatus.Accepted, (await stale.HelloAsync(token)).Status);

        // The old TCP connection is still open (e.g. half-open after a Wi-Fi drop).
        var fresh = await ConnectAsync();
        Assert.Equal(HelloStatus.Accepted, (await fresh.HelloAsync(token)).Status);

        Assert.Equal("replaced by a new connection", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(await _server.SendControlAsync(new Control { Zoom = 2 }, Ct));
        Assert.Equal(2, (await fresh.ReadUntilAsync(MessageType.Control)).ReadJson(ProtocolJson.Default.Control).Zoom);
    }

    [Fact]
    public async Task Unpaired_phone_cannot_evict_a_streaming_phone()
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Connected += _ => connected.TrySetResult();
        await ConnectPairedAsync();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // Same device id, but no valid token.
        var impostor = await ConnectAsync();
        Assert.Equal(HelloStatus.Busy, (await impostor.HelloAsync("bad-token")).Status);
    }

    [Fact]
    public async Task Wrong_protocol_version_is_rejected()
    {
        var phone = await ConnectAsync();
        var ack = await phone.HelloAsync(null, version: 99);

        Assert.Equal(HelloStatus.VersionMismatch, ack.Status);
    }

    [Fact]
    public async Task Server_answers_ping_and_reports_disconnect()
    {
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Disconnected += (_, reason) => disconnected.TrySetResult(reason);
        var phone = await ConnectPairedAsync();

        await phone.Stream.WriteAsync(Message.Empty(MessageType.Ping, 555), Ct);
        var pong = await phone.ReadUntilAsync(MessageType.Pong);
        Assert.Equal(555UL, pong.ReadPongTimestamp());

        await phone.Stream.WriteAsync(Message.Json(MessageType.Bye, new Bye("test"), ProtocolJson.Default.Bye), Ct);
        Assert.Equal("closed by phone", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Reconnecting_after_four_wrong_pins_gets_no_new_attempts()
    {
        var prompts = Channel.CreateUnbounded<PairingPrompt>();
        var ended = Channel.CreateUnbounded<bool>();
        _server.PairingStarted += p => prompts.Writer.TryWrite(p);
        _server.PairingEnded += () => ended.Writer.TryWrite(true);

        var first = await ConnectAsync();
        Assert.Equal(HelloStatus.PairingRequired, (await first.HelloAsync(null)).Status);
        var pin = (await prompts.Reader.ReadAsync(Ct)).Pin;
        for (var left = PinGuard.MaxAttempts - 1; left > 0; left--)
            Assert.Equal(left, (await first.PairAsync(WrongPin(pin))).AttemptsLeft);
        await first.DisposeAsync();
        await ended.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // A fresh connection gets a fresh PIN, but not a fresh set of attempts.
        var second = await ConnectAsync();
        Assert.Equal(HelloStatus.PairingRequired, (await second.HelloAsync(null)).Status);
        pin = (await prompts.Reader.ReadAsync(Ct)).Pin;
        var result = await second.PairAsync(WrongPin(pin));
        Assert.False(result.Ok);
        Assert.Equal(0, result.AttemptsLeft);
        Assert.True(await second.ClosedWithinAsync(TimeSpan.FromSeconds(5)));
        await ended.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var third = await ConnectAsync();
        Assert.Equal(HelloStatus.PairingLocked, (await third.HelloAsync(null)).Status);
    }

    [Fact]
    public async Task Paired_phone_evicts_a_pairing_in_progress()
    {
        var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource<ConnectedDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.PairingStarted += _ => prompt.TrySetResult();
        _server.PairingEnded += () => ended.TrySetResult();
        _server.Connected += d => connected.TrySetResult(d);

        var stranger = await ConnectAsync("stranger");
        Assert.Equal(HelloStatus.PairingRequired, (await stranger.HelloAsync(null)).Status);
        await prompt.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // Only one pairing at a time.
        var another = await ConnectAsync("another-stranger");
        Assert.Equal(HelloStatus.Busy, (await another.HelloAsync(null)).Status);
        Assert.True(await another.ClosedWithinAsync(TimeSpan.FromSeconds(5)));

        var phone = await ConnectPairedAsync();
        Assert.Equal("phone-1", (await connected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).DeviceId);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(await stranger.ClosedWithinAsync(TimeSpan.FromSeconds(5)));

        Assert.True(await _server.SendControlAsync(new Control { Torch = true }, Ct));
        Assert.True((await phone.ReadUntilAsync(MessageType.Control)).ReadJson(ProtocolJson.Default.Control).Torch);
    }

    [Fact]
    public async Task Oversized_message_before_hello_closes_the_connection()
    {
        // A long Hello timeout, so only the size check can close the connection in time.
        var server = StartServer(new HitCamServerOptions { Port = 0, BindAddress = IPAddress.Loopback, HelloTimeout = TimeSpan.FromMinutes(1) });
        var phone = await ConnectAsync(server: server);

        var header = new byte[MessageHeader.Size];
        new MessageHeader(MessageType.Hello, MessageFlags.None, MessageHeader.MaxPayloadLength, 0).Write(header);
        // Header only: a server that waited for (or allocated) the 8 MiB payload would keep the connection open.
        await phone.WriteRawAsync(header);

        Assert.True(await phone.ClosedWithinAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Oversized_json_message_ends_the_session()
    {
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Disconnected += (_, reason) => disconnected.TrySetResult(reason);
        var phone = await ConnectPairedAsync();

        await phone.Stream.WriteAsync(new Message(new MessageHeader(MessageType.Status, MessageFlags.None, PayloadLimits.Json + 1, 0),
            new byte[PayloadLimits.Json + 1]), Ct);

        Assert.Contains("limit", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Excess_connections_are_closed_right_away()
    {
        var options = new HitCamServerOptions { Port = 0, BindAddress = IPAddress.Loopback, HelloTimeout = TimeSpan.FromMinutes(1) };
        var perAddress = StartServer(options);
        var total = StartServer(options with { MaxConnections = 3, MaxHandshakesPerAddress = 10 });

        var pending = new[] { await ConnectAsync(server: perAddress), await ConnectAsync(server: perAddress) };
        Assert.True(await (await ConnectAsync(server: perAddress)).ClosedWithinAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await pending[0].ClosedWithinAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(HelloStatus.PairingRequired, (await pending[1].HelloAsync(null)).Status);

        for (var i = 0; i < 3; i++)
            await ConnectAsync(server: total);
        Assert.True(await (await ConnectAsync(server: total)).ClosedWithinAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Throwing_handlers_do_not_break_the_session()
    {
        var frames = 0;
        var twoFrames = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Connected += _ => throw new InvalidOperationException("Connected");
        _server.FrameReceived += _ => throw new InvalidOperationException("FrameReceived");
        _server.FrameReceived += _ =>
        {
            if (Interlocked.Increment(ref frames) == 2)
                twoFrames.TrySetResult();
        };
        _server.Disconnected += (_, _) => throw new InvalidOperationException("Disconnected");
        _server.Disconnected += (_, reason) => disconnected.TrySetResult(reason);

        var phone = await ConnectPairedAsync();
        for (var i = 0; i < 2; i++)
            await phone.Stream.WriteAsync(new Message(new MessageHeader(MessageType.VideoFrame, MessageFlags.None, 1, 0), [0]), Ct);
        await twoFrames.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await phone.Stream.WriteAsync(Message.Json(MessageType.Bye, new Bye("test"), ProtocolJson.Default.Bye), Ct);
        Assert.Equal("closed by phone", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Throwing_pairing_handler_still_ends_the_pairing()
    {
        var prompt = new TaskCompletionSource<PairingPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.PairingStarted += _ => throw new InvalidOperationException("PairingStarted");
        _server.PairingStarted += p => prompt.TrySetResult(p);
        _server.PairingEnded += () => throw new InvalidOperationException("PairingEnded");
        _server.PairingEnded += () => ended.TrySetResult();

        var phone = await ConnectAsync();
        Assert.Equal(HelloStatus.PairingRequired, (await phone.HelloAsync(null)).Status);
        var pin = (await prompt.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Pin;
        Assert.True((await phone.PairAsync(pin)).Ok);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Pairing_store_write_failure_does_not_break_the_handshake()
    {
        var store = new FailingStore();
        var server = StartServer(new HitCamServerOptions { Port = 0, BindAddress = IPAddress.Loopback }, store);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new TaskCompletionSource<PairingPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Connected += _ => connected.TrySetResult();
        server.PairingStarted += p => prompt.TrySetResult(p);
        var token = store.Pair("phone-1", "iPhone");
        store.Fail = true;

        // Pairing cannot be persisted: the new phone is turned away, the server keeps going.
        var newcomer = await ConnectAsync("phone-2", server);
        Assert.Equal(HelloStatus.PairingRequired, (await newcomer.HelloAsync(null)).Status);
        var pin = (await prompt.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Pin;
        await newcomer.Stream.WriteAsync(Message.Json(MessageType.PairRequest, new PairRequest(pin), ProtocolJson.Default.PairRequest), Ct);
        Assert.True(await newcomer.ClosedWithinAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(store.Devices, d => d.DeviceId == "phone-2");

        // Saving "last seen" fails too, but the paired phone still connects.
        var phone = await ConnectAsync(server: server);
        Assert.Equal(HelloStatus.Accepted, (await phone.HelloAsync(token)).Status);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_port_in_use_leaves_the_server_stopped_and_disposable()
    {
        var port = _server.Port;
        var second = new HitCamServer(new HitCamServerOptions { Port = port, BindAddress = IPAddress.Loopback }, _store);

        Assert.Throws<SocketException>(second.Start);

        Assert.False(second.IsRunning);
        await second.DisposeAsync();
    }

    private HitCamServer StartServer(HitCamServerOptions options, IPairingStore? store = null)
    {
        var server = new HitCamServer(options, store ?? new InMemoryPairingStore());
        _servers.Add(server);
        server.Start();
        return server;
    }

    private static string WrongPin(string pin) => pin == "000000" ? "000001" : "000000";

    private sealed class FailingStore : InMemoryPairingStore
    {
        public volatile bool Fail;

        protected override void Save(IReadOnlyList<PairedDevice> devices)
        {
            if (Fail)
                throw new IOException("Disk full.");
        }
    }

    private async Task<TestPhone> ConnectPairedAsync()
    {
        var token = _store.Pair("phone-1", "iPhone");
        var phone = await ConnectAsync();
        Assert.Equal(HelloStatus.Accepted, (await phone.HelloAsync(token)).Status);
        return phone;
    }

    private async Task<TestPhone> ConnectAsync(string deviceId = "phone-1", HitCamServer? server = null)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, (server ?? _server).Port, Ct);
        var phone = new TestPhone(client, deviceId);
        _phones.Add(phone);
        return phone;
    }
}

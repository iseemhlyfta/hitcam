using System.Net;
using System.Net.Sockets;
using HitCam.Core.Pairing;
using HitCam.Core.Protocol;
using HitCam.Core.Server;

namespace HitCam.Core.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    private readonly InMemoryPairingStore _store = new();
    private HitCamServer _server = null!;
    private readonly List<FakePhone> _phones = [];
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

    private async Task<FakePhone> ConnectPairedAsync()
    {
        var token = _store.Pair("phone-1", "iPhone");
        var phone = await ConnectAsync();
        Assert.Equal(HelloStatus.Accepted, (await phone.HelloAsync(token)).Status);
        return phone;
    }

    private async Task<FakePhone> ConnectAsync(string deviceId = "phone-1")
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.Port, Ct);
        var phone = new FakePhone(client, deviceId);
        _phones.Add(phone);
        return phone;
    }

    private sealed class FakePhone(TcpClient client, string deviceId) : IAsyncDisposable
    {
        public MessageStream Stream { get; } = new(client.GetStream());

        public async Task<HelloAck> HelloAsync(string? token, int version = ProtocolInfo.Version)
        {
            await Stream.WriteAsync(Message.Json(MessageType.Hello, new Hello(version, deviceId, "iPhone", Token: token), ProtocolJson.Default.Hello), Ct);
            return (await ReadUntilAsync(MessageType.HelloAck)).ReadJson(ProtocolJson.Default.HelloAck);
        }

        public async Task<PairResult> PairAsync(string pin)
        {
            await Stream.WriteAsync(Message.Json(MessageType.PairRequest, new PairRequest(pin), ProtocolJson.Default.PairRequest), Ct);
            return (await ReadUntilAsync(MessageType.PairResult)).ReadJson(ProtocolJson.Default.PairResult);
        }

        /// <summary>Skips pings and other chatter until a message of the given type arrives.</summary>
        public async Task<Message> ReadUntilAsync(MessageType type)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                var message = await Stream.ReadAsync(timeout.Token) ?? throw new EndOfStreamException();
                if (message.Type == type)
                    return message;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            client.Dispose();
        }
    }
}

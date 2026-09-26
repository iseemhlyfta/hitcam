using System.Net;
using System.Security.Cryptography;
using HitCam.Core.Pairing;
using HitCam.Core.Protocol;
using HitCam.Core.Security;
using HitCam.Core.Server;

namespace HitCam.Core.Tests;

public sealed class TlsServerTests : IAsyncLifetime
{
    private static readonly ServerIdentity Identity = ServerIdentity.Create();
    private readonly InMemoryPairingStore _store = new();
    private readonly List<TestPhone> _phones = [];
    private readonly List<HitCamServer> _servers = [];
    private readonly System.Threading.Channels.Channel<PairingPrompt> _prompts = System.Threading.Channels.Channel.CreateUnbounded<PairingPrompt>();
    private HitCamServer _server = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _server = Start(allowPlaintext: true);
        _server.PairingStarted += p => _prompts.Writer.TryWrite(p);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var phone in _phones)
            await phone.DisposeAsync();
        foreach (var server in _servers)
            await server.DisposeAsync();
    }

    [Fact]
    public async Task A_phone_pairs_over_tls_and_the_pc_shows_the_pin_it_proves()
    {
        var connected = new TaskCompletionSource<ConnectedDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Connected += d => connected.TrySetResult(d);

        var phone = await ConnectTlsAsync();
        Assert.Equal(Identity.Fingerprint, phone.SeenFingerprint);
        var ack = await phone.HelloAsync(token: null);
        Assert.Equal(HelloStatus.PairingRequired, ack.Status);
        Assert.Equal(ProtocolInfo.Version, ack.ProtocolVersion);
        Assert.NotNull(ack.PinCommit);

        var pin = await PinAsync();
        var result = await phone.PairOverTlsAsync(ack.PinCommit!, pin);
        Assert.True(result?.Ok);
        Assert.True((await connected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Encrypted);
        await phone.DisposeAsync();

        var again = await ConnectTlsAsync();
        Assert.Equal(HelloStatus.Accepted, (await again.HelloAsync(result!.Token)).Status);
    }

    [Fact]
    public async Task A_mistyped_pin_fails_on_both_sides_and_uses_the_pin_up()
    {
        var phone = await ConnectTlsAsync();
        var ack = await phone.HelloAsync(token: null);
        var pin = await PinAsync();
        var typo = pin == "000000" ? "000001" : "000000";

        // The phone sees the PC's commitment does not match what it typed and stops: it never opens its own.
        Assert.Null(await phone.PairOverTlsAsync(ack.PinCommit!, typo));
        await phone.DisposeAsync();

        // That counted as a wrong PIN, and the next connection gets a new pairing window.
        var next = await ConnectTlsAsync();
        Assert.Equal(HelloStatus.PairingRequired, (await next.HelloAsync(token: null)).Status);
        var nextPin = await PinAsync();
        Assert.Equal(PinGuard.MaxAttempts - 2, await AttemptsLeftAfterWrongPinAsync(next, nextPin));
        Assert.Empty(_store.Devices);
    }

    [Fact]
    public async Task A_middleman_with_its_own_certificate_cannot_pass_the_pin()
    {
        // The phone talks TLS to a middleman: the fingerprint it sees is the middleman's, not the PC's. Even with the
        // right PIN (read off the PC screen by the user) the commitments do not match.
        var phone = await ConnectTlsAsync();
        var ack = await phone.HelloAsync(token: null);
        var pin = await PinAsync();
        using var middleman = ServerIdentity.Create();

        Assert.Null(await phone.PairOverTlsAsync(ack.PinCommit!, pin, middleman.Fingerprint));
        Assert.Empty(_store.Devices);
    }

    [Fact]
    public async Task A_phone_that_commits_to_the_wrong_pin_gets_no_token()
    {
        // A phone that ignores the PC's commitment and confirms anyway: its own commitment is checked on the PC.
        var phone = await ConnectTlsAsync();
        await phone.HelloAsync(token: null);
        Assert.Equal(PinGuard.MaxAttempts - 1, await AttemptsLeftAfterWrongPinAsync(phone, await PinAsync()));
        Assert.True(await phone.ClosedWithinAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(_store.Devices);
    }

    [Fact]
    public async Task Versions_do_not_cross_transports()
    {
        var tlsV1 = await ConnectTlsAsync();
        Assert.Equal(HelloStatus.VersionMismatch, (await tlsV1.HelloAsync(null, ProtocolInfo.LegacyVersion)).Status);

        var plainV2 = await ConnectPlainAsync(_server);
        Assert.Equal(HelloStatus.VersionMismatch, (await plainV2.HelloAsync(null, ProtocolInfo.Version)).Status);
    }

    [Fact]
    public async Task Old_phones_on_plain_tcp_are_let_in_unless_turned_off()
    {
        var connected = new TaskCompletionSource<ConnectedDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Connected += d => connected.TrySetResult(d);
        var old = await ConnectPlainAsync(_server);
        Assert.Equal(HelloStatus.PairingRequired, (await old.HelloAsync(null)).Status);
        Assert.True((await old.PairAsync(await PinAsync())).Ok);
        Assert.False((await connected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Encrypted);

        var strict = Start(allowPlaintext: false);
        var refused = await ConnectPlainAsync(strict);
        Assert.Equal(HelloStatus.VersionMismatch, (await refused.HelloAsync(null)).Status);
    }

    [Fact]
    public void An_identity_survives_export_and_load()
    {
        using var loaded = ServerIdentity.Load(Identity.Export());
        Assert.Equal(Identity.Fingerprint, loaded.Fingerprint);
        Assert.True(loaded.Certificate.HasPrivateKey);
        Assert.Throws<CryptographicException>(() => ServerIdentity.Load([1, 2, 3]));
    }

    [Fact]
    public void Commitments_bind_every_input()
    {
        var fp = SHA256.HashData([1]);
        var nonce = new byte[PinProof.NonceSize];
        var baseline = PinProof.Commit(PinProof.PcLabel, fp, "123456", nonce);
        Assert.NotEqual(baseline, PinProof.Commit(PinProof.PhoneLabel, fp, "123456", nonce));
        Assert.NotEqual(baseline, PinProof.Commit(PinProof.PcLabel, SHA256.HashData([2]), "123456", nonce));
        Assert.NotEqual(baseline, PinProof.Commit(PinProof.PcLabel, fp, "123457", nonce));
        nonce[31] = 1;
        Assert.NotEqual(baseline, PinProof.Commit(PinProof.PcLabel, fp, "123456", nonce));
    }

    /// <summary>Commits to a PIN that is surely wrong and confirms regardless; the PC's answer (attempts left).</summary>
    private static async Task<int> AttemptsLeftAfterWrongPinAsync(TestPhone phone, string pin)
    {
        var wrong = pin == "000000" ? "999999" : "000000";
        var nonce = PinProof.NewNonce();
        var commit = PinProof.Commit(PinProof.PhoneLabel, phone.SeenFingerprint!, wrong, nonce);
        await phone.Stream.WriteAsync(Message.Json(MessageType.PairRequest, new PairRequest(Commit: Convert.ToHexStringLower(commit)), ProtocolJson.Default.PairRequest), Ct);
        await phone.ReadUntilAsync(MessageType.PairReveal);
        await phone.Stream.WriteAsync(Message.Json(MessageType.PairConfirm, new PairNonce(Convert.ToHexStringLower(nonce)), ProtocolJson.Default.PairNonce), Ct);
        var result = (await phone.ReadUntilAsync(MessageType.PairResult)).ReadJson(ProtocolJson.Default.PairResult);
        Assert.False(result.Ok);
        Assert.Null(result.Token);
        return result.AttemptsLeft;
    }

    /// <summary>The PIN of the next pairing window as shown on the PC.</summary>
    private async Task<string> PinAsync() =>
        (await _prompts.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct)).Pin;

    private HitCamServer Start(bool allowPlaintext)
    {
        var server = new HitCamServer(
            new HitCamServerOptions { Port = 0, BindAddress = IPAddress.Loopback, Identity = Identity, AllowPlaintext = allowPlaintext },
            _store);
        server.Start();
        _servers.Add(server);
        return server;
    }

    private async Task<TestPhone> ConnectTlsAsync()
    {
        var phone = await TestPhone.ConnectTlsAsync(_server.Port, "phone-1");
        _phones.Add(phone);
        return phone;
    }

    private async Task<TestPhone> ConnectPlainAsync(HitCamServer server)
    {
        var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, Ct);
        var phone = new TestPhone(client, "phone-1");
        _phones.Add(phone);
        return phone;
    }

}

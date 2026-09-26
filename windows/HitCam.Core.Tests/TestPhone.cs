using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using HitCam.Core.Protocol;
using HitCam.Core.Security;

namespace HitCam.Core.Tests;

/// <summary>Minimal phone side of the protocol for server tests: plain v1, or v2 over TLS.</summary>
internal sealed class TestPhone(TcpClient client, string deviceId, Stream? transport = null) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Stream _transport = transport ?? client.GetStream();

    public MessageStream Stream { get; } = new(transport ?? client.GetStream());

    /// <summary>Fingerprint of the certificate the TLS connection presented (v2), else null.</summary>
    public byte[]? SeenFingerprint { get; private init; }

    public int Version => SeenFingerprint is null ? ProtocolInfo.LegacyVersion : ProtocolInfo.Version;

    /// <summary>Connects over TLS, accepting any certificate and recording its fingerprint, as a phone pairing does.</summary>
    public static async Task<TestPhone> ConnectTlsAsync(int port, string deviceId)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, Ct);
        byte[]? seen = null;
        var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
        {
            seen = certificate is null ? null : ServerIdentity.FingerprintOf(certificate);
            return certificate is not null;
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "hitcam" }, Ct);
        return new TestPhone(client, deviceId, tls) { SeenFingerprint = seen };
    }

    public async Task<HelloAck> HelloAsync(string? token, int? version = null)
    {
        await Stream.WriteAsync(Message.Json(MessageType.Hello, new Hello(version ?? Version, deviceId, "iPhone", Token: token), ProtocolJson.Default.Hello), Ct);
        return (await ReadUntilAsync(MessageType.HelloAck)).ReadJson(ProtocolJson.Default.HelloAck);
    }

    public async Task<PairResult> PairAsync(string pin)
    {
        await Stream.WriteAsync(Message.Json(MessageType.PairRequest, new PairRequest(pin), ProtocolJson.Default.PairRequest), Ct);
        return (await ReadUntilAsync(MessageType.PairResult)).ReadJson(ProtocolJson.Default.PairResult);
    }

    /// <summary>
    /// v2 pairing as the phone does it: commits to the typed PIN and the fingerprint it saw, checks the PC's
    /// commitment once opened, and opens its own only if that matched. Null when the PC's commitment did not match.
    /// </summary>
    public async Task<PairResult?> PairOverTlsAsync(string pinCommitHex, string typedPin, byte[]? fingerprint = null)
    {
        var seen = fingerprint ?? SeenFingerprint!;
        var nonce = PinProof.NewNonce();
        var commit = PinProof.Commit(PinProof.PhoneLabel, seen, typedPin, nonce);
        await Stream.WriteAsync(Message.Json(MessageType.PairRequest, new PairRequest(Commit: Convert.ToHexStringLower(commit)), ProtocolJson.Default.PairRequest), Ct);
        var reveal = (await ReadUntilAsync(MessageType.PairReveal)).ReadJson(ProtocolJson.Default.PairNonce);
        var expected = PinProof.Commit(PinProof.PcLabel, seen, typedPin, Convert.FromHexString(reveal.Nonce));
        if (Convert.ToHexStringLower(expected) != pinCommitHex)
            return null;
        await Stream.WriteAsync(Message.Json(MessageType.PairConfirm, new PairNonce(Convert.ToHexStringLower(nonce)), ProtocolJson.Default.PairNonce), Ct);
        return (await ReadUntilAsync(MessageType.PairResult)).ReadJson(ProtocolJson.Default.PairResult);
    }

    /// <summary>Writes bytes as they are, bypassing the framing checks of <see cref="MessageStream"/>.</summary>
    public async Task WriteRawAsync(byte[] bytes)
    {
        await _transport.WriteAsync(bytes, Ct);
        await _transport.FlushAsync(Ct);
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

    /// <summary>True when the server closes the connection within the given time (skipping anything it still sends).</summary>
    public async Task<bool> ClosedWithinAsync(TimeSpan time)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(time);
        try
        {
            while (await Stream.ReadAsync(timeout.Token) is not null)
            {
            }
            return true;
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        client.Dispose();
    }
}

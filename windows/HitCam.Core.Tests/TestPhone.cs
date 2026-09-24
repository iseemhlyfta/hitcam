using System.Net.Sockets;
using HitCam.Core.Protocol;

namespace HitCam.Core.Tests;

/// <summary>Minimal phone side of the protocol for server tests.</summary>
internal sealed class TestPhone(TcpClient client, string deviceId) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    /// <summary>Writes bytes as they are, bypassing the framing checks of <see cref="MessageStream"/>.</summary>
    public async Task WriteRawAsync(byte[] bytes)
    {
        var stream = client.GetStream();
        await stream.WriteAsync(bytes, Ct);
        await stream.FlushAsync(Ct);
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

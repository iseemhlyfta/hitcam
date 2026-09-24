namespace HitCam.Core.Protocol;

/// <summary>
/// Reads and writes framed messages over a byte stream. Reads must come from a single consumer;
/// writes are serialized internally, so several producers may send concurrently.
/// </summary>
public sealed class MessageStream(Stream stream) : IAsyncDisposable
{
    private readonly byte[] _readHeader = new byte[MessageHeader.Size];
    private readonly byte[] _writeHeader = new byte[MessageHeader.Size];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Returns the next message, or null when the peer closed the stream cleanly between messages.</summary>
    public ValueTask<Message?> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(static _ => MessageHeader.MaxPayloadLength, cancellationToken);

    /// <summary>
    /// Like <see cref="ReadAsync(CancellationToken)"/>, but rejects a payload longer than
    /// <paramref name="maxLength"/> allows for its type before allocating a buffer for it.
    /// </summary>
    public async ValueTask<Message?> ReadAsync(Func<MessageType, int> maxLength, CancellationToken cancellationToken = default)
    {
        var first = await stream.ReadAtLeastAsync(_readHeader, MessageHeader.Size, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (first == 0)
            return null;
        if (first < MessageHeader.Size)
            throw new EndOfStreamException("Connection closed in the middle of a message header.");

        var header = MessageHeader.Read(_readHeader);
        var limit = maxLength(header.Type);
        if (header.Length > limit)
            throw new ProtocolException($"{header.Type} payload of {header.Length} bytes exceeds the {limit} byte limit here.");
        var payload = header.Length == 0 ? [] : new byte[header.Length];
        if (payload.Length > 0)
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        return new Message(header, payload);
    }

    public async ValueTask WriteAsync(Message message, CancellationToken cancellationToken = default)
    {
        if (message.Header.Length != message.Payload.Length)
            throw new ArgumentException("Header length does not match the payload.", nameof(message));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            message.Header.Write(_writeHeader);
            await stream.WriteAsync(_writeHeader, cancellationToken).ConfigureAwait(false);
            if (message.Payload.Length > 0)
                await stream.WriteAsync(message.Payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}

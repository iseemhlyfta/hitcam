using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace HitCam.Core.Protocol;

/// <summary>A decoded protocol message: header plus its payload.</summary>
public sealed record Message(MessageHeader Header, byte[] Payload)
{
    public MessageType Type => Header.Type;
    public bool IsKeyframe => Header.Flags.HasFlag(MessageFlags.Keyframe);

    public T ReadJson<T>(JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(Payload, typeInfo)
                   ?? throw new ProtocolException($"{Type} payload is null.");
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"{Type} payload is not valid JSON: {ex.Message}");
        }
    }

    public static Message Json<T>(MessageType type, T value, JsonTypeInfo<T> typeInfo, ulong timestamp = 0)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        return new Message(new MessageHeader(type, MessageFlags.None, payload.Length, timestamp), payload);
    }

    public static Message Empty(MessageType type, ulong timestamp = 0) =>
        new(new MessageHeader(type, MessageFlags.None, 0, timestamp), []);

    public static Message Pong(ulong pingTimestamp, ulong timestamp)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, pingTimestamp);
        return new Message(new MessageHeader(MessageType.Pong, MessageFlags.None, 8, timestamp), payload);
    }

    public ulong ReadPongTimestamp()
    {
        if (Type != MessageType.Pong || Payload.Length != 8)
            throw new ProtocolException("Pong payload must be exactly 8 bytes.");
        return BinaryPrimitives.ReadUInt64LittleEndian(Payload);
    }
}

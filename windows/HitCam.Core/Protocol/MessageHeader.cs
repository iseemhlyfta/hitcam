using System.Buffers.Binary;

namespace HitCam.Core.Protocol;

/// <summary>Message types of HitCam protocol v1. See docs/protocol.md.</summary>
public enum MessageType : byte
{
    Hello = 0x01,
    HelloAck = 0x02,
    PairRequest = 0x03,
    PairResult = 0x04,
    StreamConfig = 0x10,
    VideoFrame = 0x11,
    RequestKeyframe = 0x12,
    Capabilities = 0x20,
    CameraState = 0x21,
    Control = 0x22,
    Status = 0x30,
    Ping = 0x31,
    Pong = 0x32,
    Bye = 0x3F,
}

[Flags]
public enum MessageFlags : byte
{
    None = 0,
    /// <summary>VideoFrame: the payload starts an IDR access unit (SPS/PPS included).</summary>
    Keyframe = 1 << 0,
}

/// <summary>Fixed 16-byte message header, little-endian.</summary>
public readonly record struct MessageHeader(MessageType Type, MessageFlags Flags, int Length, ulong Timestamp)
{
    public const int Size = 16;
    public const int MaxPayloadLength = 8 * 1024 * 1024;

    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException("Destination is too small for a header.", nameof(destination));
        if ((uint)Length > MaxPayloadLength)
            throw new ProtocolException($"Payload length {Length} exceeds the {MaxPayloadLength} byte limit.");

        destination[0] = (byte)Type;
        destination[1] = (byte)Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], Length);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], Timestamp);
    }

    public static MessageHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException("Source is too small for a header.", nameof(source));

        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(source[2..]);
        if (reserved != 0)
            throw new ProtocolException("Reserved header field must be zero.");

        var length = BinaryPrimitives.ReadInt32LittleEndian(source[4..]);
        if ((uint)length > MaxPayloadLength)
            throw new ProtocolException($"Payload length {length} exceeds the {MaxPayloadLength} byte limit.");

        return new MessageHeader(
            (MessageType)source[0],
            (MessageFlags)source[1],
            length,
            BinaryPrimitives.ReadUInt64LittleEndian(source[8..]));
    }
}

/// <summary>
/// Per-state payload limits, checked before the payload buffer is allocated, so a peer that has not
/// finished the handshake cannot make the receiver reserve megabytes.
/// </summary>
public static class PayloadLimits
{
    /// <summary>Hello and PairRequest, before the session is established.</summary>
    public const int Handshake = 4 * 1024;
    /// <summary>JSON and other small messages of an established session.</summary>
    public const int Json = 64 * 1024;
    /// <summary>VideoFrame of an established session.</summary>
    public const int VideoFrame = MessageHeader.MaxPayloadLength;

    public static int ForSession(MessageType type) => type == MessageType.VideoFrame ? VideoFrame : Json;
}

public sealed class ProtocolException(string message) : Exception(message);

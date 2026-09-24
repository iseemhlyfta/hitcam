using HitCam.Core.Protocol;

namespace HitCam.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Header_round_trips()
    {
        var header = new MessageHeader(MessageType.VideoFrame, MessageFlags.Keyframe, 1234, 0x0102030405060708);
        var buffer = new byte[MessageHeader.Size];
        header.Write(buffer);

        Assert.Equal([0x11, 0x01, 0, 0, 0xD2, 0x04, 0, 0, 8, 7, 6, 5, 4, 3, 2, 1], buffer);
        Assert.Equal(header, MessageHeader.Read(buffer));
    }

    [Fact]
    public void Header_rejects_non_zero_reserved_field()
    {
        var buffer = new byte[MessageHeader.Size];
        new MessageHeader(MessageType.Ping, MessageFlags.None, 0, 0).Write(buffer);
        buffer[2] = 1;

        Assert.Throws<ProtocolException>(() => MessageHeader.Read(buffer));
    }

    [Theory]
    [InlineData(MessageHeader.MaxPayloadLength + 1)]
    [InlineData(-1)]
    public void Header_rejects_oversized_payload(int length)
    {
        var buffer = new byte[MessageHeader.Size];
        new MessageHeader(MessageType.Ping, MessageFlags.None, 0, 0).Write(buffer);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), length);

        Assert.Throws<ProtocolException>(() => MessageHeader.Read(buffer));
    }

    [Fact]
    public async Task Stream_reads_messages_split_into_single_bytes()
    {
        var bytes = new MemoryStream();
        var writer = new MessageStream(bytes);
        await writer.WriteAsync(Message.Json(MessageType.StreamConfig, new StreamConfig("h264", 1920, 1080, 60, 8000), ProtocolJson.Default.StreamConfig, 42),
            TestContext.Current.CancellationToken);
        await writer.WriteAsync(new Message(new MessageHeader(MessageType.VideoFrame, MessageFlags.Keyframe, 3, 43), [1, 2, 3]),
            TestContext.Current.CancellationToken);

        var reader = new MessageStream(new TrickleStream(bytes.ToArray()));
        var config = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var frame = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var end = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new StreamConfig("h264", 1920, 1080, 60, 8000), config!.ReadJson(ProtocolJson.Default.StreamConfig));
        Assert.Equal(42UL, config.Header.Timestamp);
        Assert.True(frame!.IsKeyframe);
        Assert.Equal([1, 2, 3], frame.Payload);
        Assert.Null(end);
    }

    [Fact]
    public async Task Stream_throws_when_connection_drops_mid_header()
    {
        var reader = new MessageStream(new MemoryStream([0x31, 0, 0]));

        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public void Json_payload_uses_camel_case_and_skips_nulls()
    {
        var message = Message.Json(MessageType.Control, new Control { Zoom = 2, Torch = true }, ProtocolJson.Default.Control);

        Assert.Equal("""{"zoom":2,"torch":true}""", System.Text.Encoding.UTF8.GetString(message.Payload));
    }

    [Fact]
    public void Invalid_json_payload_is_a_protocol_error()
    {
        var message = new Message(new MessageHeader(MessageType.Hello, MessageFlags.None, 3, 0), "{x}"u8.ToArray());

        Assert.Throws<ProtocolException>(() => message.ReadJson(ProtocolJson.Default.Hello));
    }

    [Fact]
    public void Camera_state_from_an_older_phone_app_has_no_new_settings()
    {
        var json = """{"cameraId":"back-wide","zoom":1,"torch":false,"focusMode":"continuous","lensPosition":0.5,"exposureBias":0,"mirror":false,"rotation":0,"width":1920,"height":1080,"fps":30,"bitrateKbps":8000}"""u8;
        var state = new Message(new MessageHeader(MessageType.CameraState, MessageFlags.None, json.Length, 0), json.ToArray())
            .ReadJson(ProtocolJson.Default.CameraState);

        Assert.Equal("back-wide", state.CameraId);
        Assert.Null(state.WhiteBalanceMode);
        Assert.Null(state.StabilizationModes);
    }

    [Fact]
    public void White_balance_exposure_and_stabilization_round_trip()
    {
        var state = new CameraState("back-wide", 1, false, "continuous", 0.5, 0, false, 0, 1920, 1080, 30, 8000,
            WhiteBalanceModes.Locked, 4200, -10, ExposureModes.Locked, StabilizationModes.Standard,
            [StabilizationModes.Off, StabilizationModes.Standard]);
        var control = new Control { WhiteBalanceMode = WhiteBalanceModes.Locked, WhiteBalanceTemperature = 4200, ExposureMode = ExposureModes.Auto };

        var stateBack = Message.Json(MessageType.CameraState, state, ProtocolJson.Default.CameraState).ReadJson(ProtocolJson.Default.CameraState);
        var controlJson = System.Text.Encoding.UTF8.GetString(Message.Json(MessageType.Control, control, ProtocolJson.Default.Control).Payload);

        Assert.Equal((4200.0, -10.0, "standard"), (stateBack.WhiteBalanceTemperature, stateBack.WhiteBalanceTint, stateBack.Stabilization));
        Assert.Equal(["off", "standard"], stateBack.StabilizationModes!);
        Assert.Equal("""{"whiteBalanceMode":"locked","whiteBalanceTemperature":4200,"exposureMode":"auto"}""", controlJson);
    }

    [Fact]
    public void Pong_echoes_ping_timestamp()
    {
        var pong = Message.Pong(123456789, 5);

        Assert.Equal(123456789UL, pong.ReadPongTimestamp());
        Assert.Equal(5UL, pong.Header.Timestamp);
    }

    /// <summary>Returns at most one byte per read, like a very fragmented TCP stream.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}

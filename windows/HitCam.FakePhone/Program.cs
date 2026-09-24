// Development tool: pretends to be the phone app so the PC side can be tested without a phone.
// Usage: HitCam.FakePhone [host] [port] [--pin 123456 | --pin-file path] [--seconds 30] [--video file.h264 --size 1280x720]
// Frames are dummy (not decodable) H.264-shaped access units at 30 fps, ~8 Mbit/s, or with --video a real H.264
// Annex B stream played in a loop: each access unit must start with an access unit delimiter (x264: aud=1) and the
// stream with an IDR frame.

using System.Diagnostics;
using System.Net.Sockets;
using HitCam.Core.Protocol;

var host = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "127.0.0.1";
var port = args.Where(a => !a.StartsWith("--")).Skip(1).Select(int.Parse).FirstOrDefault(ProtocolInfo.DefaultPort);
var pinArg = Option("--pin");
var seconds = int.Parse(Option("--seconds") ?? "30");
var video = Option("--video") is { } videoPath ? SplitAccessUnits(File.ReadAllBytes(videoPath)) : null;
var size = (Option("--size") ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
var tokenFile = Path.Combine(Path.GetTempPath(), "hitcam-fakephone.token");
const string deviceId = "fake-phone-0001";

var clock = Stopwatch.StartNew();
ulong Now() => (ulong)(clock.Elapsed.Ticks / 10);

using var tcp = new TcpClient { NoDelay = true };
await tcp.ConnectAsync(host, port);
await using var stream = new MessageStream(tcp.GetStream());
Console.WriteLine($"Connected to {host}:{port}");

var token = File.Exists(tokenFile) ? File.ReadAllText(tokenFile).Trim() : null;
await stream.WriteAsync(Message.Json(MessageType.Hello,
    new Hello(ProtocolInfo.Version, deviceId, "Fake iPhone", "FakePhone1,1", "0.1.0", token), ProtocolJson.Default.Hello, Now()));

var ack = (await ReadAsync(MessageType.HelloAck)).ReadJson(ProtocolJson.Default.HelloAck);
Console.WriteLine($"HelloAck: {ack.Status} from {ack.ServerName}");

if (ack.Status == HelloStatus.PairingRequired)
{
    while (true)
    {
        var pin = pinArg;
        if (pin is null && Option("--pin-file") is { } pinFile)
        {
            Console.WriteLine($"Waiting for the PIN in {pinFile}...");
            while (!File.Exists(pinFile))
                await Task.Delay(200);
            pin = File.ReadAllText(pinFile).Trim();
            File.Delete(pinFile);
        }
        else if (pin is null)
        {
            Console.Write("PIN shown on the PC: ");
            pin = Console.ReadLine()?.Trim();
        }
        pinArg = null;
        await stream.WriteAsync(Message.Json(MessageType.PairRequest, new PairRequest(pin ?? ""), ProtocolJson.Default.PairRequest, Now()));
        var result = (await ReadAsync(MessageType.PairResult)).ReadJson(ProtocolJson.Default.PairResult);
        if (result.Ok)
        {
            File.WriteAllText(tokenFile, result.Token);
            Console.WriteLine("Paired.");
            break;
        }
        Console.WriteLine($"Wrong PIN, {result.AttemptsLeft} attempts left.");
        if (result.AttemptsLeft == 0)
            return 1;
    }
}
else if (ack.Status != HelloStatus.Accepted)
{
    return 1;
}

await stream.WriteAsync(Message.Json(MessageType.StreamConfig, new StreamConfig("h264", size[0], size[1], 30, 8000), ProtocolJson.Default.StreamConfig, Now()));
await stream.WriteAsync(Message.Json(MessageType.Status, new Status(0.8, true, "nominal", 30, 8000, 0), ProtocolJson.Default.Status, Now()));

// Cameras and state like a phone with three back lenses, so the PC's camera settings can be exercised.
var capabilities = new Capabilities(
    [
        new CameraInfo("back-wide", "Wide", "back", 1, 10, true, true, true, true),
        new CameraInfo("back-ultrawide", "Ultra Wide", "back", 1, 10, true, false, true, true),
        new CameraInfo("back-tele", "Telephoto", "back", 1, 10, true, true, true, true),
        new CameraInfo("front", "Front", "front", 1, 5, false, false, true, true),
    ],
    [new VideoPreset(1280, 720, [30, 60]), new VideoPreset(1920, 1080, [30, 60])]);
var cameraState = new CameraState("back-wide", 1, false, "continuous", 0.5, 0, false, 0, 1920, 1080, 30, 8000,
    WhiteBalanceModes.Auto, 5200, 0, ExposureModes.Auto, StabilizationModes.Off,
    [StabilizationModes.Off, StabilizationModes.Standard, StabilizationModes.Cinematic]);
await stream.WriteAsync(Message.Json(MessageType.Capabilities, capabilities, ProtocolJson.Default.Capabilities, Now()));
await stream.WriteAsync(Message.Json(MessageType.CameraState, cameraState, ProtocolJson.Default.CameraState, Now()));

// Answer pings, apply controls and report the new state in the background.
_ = Task.Run(async () =>
{
    try
    {
        while (await stream.ReadAsync() is { } message)
        {
            if (message.Type == MessageType.Ping)
                await stream.WriteAsync(Message.Pong(message.Header.Timestamp, Now()));
            else if (message.Type == MessageType.Control)
            {
                Console.WriteLine($"Control: {System.Text.Encoding.UTF8.GetString(message.Payload)}");
                cameraState = Apply(cameraState, message.ReadJson(ProtocolJson.Default.Control));
                await stream.WriteAsync(Message.Json(MessageType.CameraState, cameraState, ProtocolJson.Default.CameraState, Now()));
            }
            else if (message.Type == MessageType.RequestKeyframe)
                Console.WriteLine("Keyframe requested");
        }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
    }
});

var random = new Random(1);
var frameInterval = TimeSpan.FromSeconds(1.0 / 30);
var start = clock.Elapsed;
var end = start + TimeSpan.FromSeconds(seconds);
for (var i = 0; clock.Elapsed < end; i++)
{
    bool keyframe;
    byte[] payload;
    if (video is not null)
    {
        payload = video[i % video.Count];
        keyframe = HasIdr(payload);
    }
    else
    {
        keyframe = i % 60 == 0;
        payload = new byte[keyframe ? 120_000 : 30_000];
        random.NextBytes(payload);
        byte[] prefix = keyframe ? [0, 0, 0, 1, 0x67, 0, 0, 0, 1, 0x68, 0, 0, 0, 1, 0x65] : [0, 0, 0, 1, 0x41];
        prefix.CopyTo(payload, 0);
    }
    await stream.WriteAsync(new Message(new MessageHeader(MessageType.VideoFrame, keyframe ? MessageFlags.Keyframe : MessageFlags.None, payload.Length, Now()), payload));
    // Pace against the clock: Task.Delay alone has ~15 ms granularity on Windows.
    var wait = start + frameInterval * (i + 1) - clock.Elapsed;
    if (wait > TimeSpan.Zero)
        await Task.Delay(wait);
}

await stream.WriteAsync(Message.Json(MessageType.Bye, new Bye("done"), ProtocolJson.Default.Bye, Now()));
Console.WriteLine("Done.");
return 0;

async Task<Message> ReadAsync(MessageType type)
{
    while (true)
    {
        var message = await stream.ReadAsync() ?? throw new EndOfStreamException("PC closed the connection.");
        if (message.Type == type)
            return message;
        if (message.Type == MessageType.Ping)
            await stream.WriteAsync(Message.Pong(message.Header.Timestamp, Now()));
    }
}

// Access units of an Annex B stream, split at the access unit delimiters (NAL type 9).
static List<byte[]> SplitAccessUnits(byte[] data)
{
    var starts = new List<int>();
    foreach (var (start, type) in NalUnits(data))
    {
        if (type == 9)
            starts.Add(start);
    }
    if (starts.Count == 0)
        throw new InvalidDataException("The video has no access unit delimiters; encode it with x264 aud=1.");
    starts.Add(data.Length);
    return [.. starts.Zip(starts.Skip(1), (from, to) => data[from..to])];
}

static bool HasIdr(byte[] accessUnit) => NalUnits(accessUnit).Any(n => n.Type == 5);

// Start of each NAL unit's start code (00 00 01, or 00 00 00 01) and its type.
static IEnumerable<(int Start, int Type)> NalUnits(byte[] data)
{
    for (var i = 0; i + 3 < data.Length; i++)
    {
        if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
            continue;
        var start = i > 0 && data[i - 1] == 0 ? i - 1 : i;
        yield return (start, data[i + 3] & 0x1F);
        i += 2;
    }
}

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

// Mirrors the phone: only the fields present in the control change; a lens switch resets zoom and focus.
static CameraState Apply(CameraState state, Control control)
{
    var next = state with
    {
        CameraId = control.CameraId ?? state.CameraId,
        Zoom = control.Zoom ?? state.Zoom,
        Torch = control.Torch ?? state.Torch,
        FocusMode = control.FocusMode ?? state.FocusMode,
        LensPosition = control.LensPosition ?? state.LensPosition,
        ExposureBias = control.ExposureBias ?? state.ExposureBias,
        Mirror = control.Mirror ?? state.Mirror,
        Rotation = control.Rotation ?? state.Rotation,
        Width = control.Width ?? state.Width,
        Height = control.Height ?? state.Height,
        Fps = control.Fps ?? state.Fps,
        BitrateKbps = control.BitrateKbps ?? state.BitrateKbps,
        WhiteBalanceMode = control.WhiteBalanceMode ?? (control.WhiteBalanceTemperature is not null || control.WhiteBalanceTint is not null ? WhiteBalanceModes.Locked : state.WhiteBalanceMode),
        WhiteBalanceTemperature = control.WhiteBalanceTemperature ?? state.WhiteBalanceTemperature,
        WhiteBalanceTint = control.WhiteBalanceTint ?? state.WhiteBalanceTint,
        ExposureMode = control.ExposureMode ?? state.ExposureMode,
        Stabilization = control.Stabilization ?? state.Stabilization,
    };
    return next.CameraId != state.CameraId ? next with { Zoom = 1, Torch = false, FocusMode = "continuous" } : next;
}

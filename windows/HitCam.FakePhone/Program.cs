// Development tool: pretends to be the iPhone app so the PC side can be tested without a phone.
// Usage: HitCam.FakePhone [host] [port] [--pin 123456 | --pin-file path] [--seconds 30]
// Frames are dummy (not decodable) H.264-shaped access units at 30 fps, ~8 Mbit/s.

using System.Diagnostics;
using System.Net.Sockets;
using HitCam.Core.Protocol;

var host = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "127.0.0.1";
var port = args.Where(a => !a.StartsWith("--")).Skip(1).Select(int.Parse).FirstOrDefault(ProtocolInfo.DefaultPort);
var pinArg = Option("--pin");
var seconds = int.Parse(Option("--seconds") ?? "30");
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

await stream.WriteAsync(Message.Json(MessageType.StreamConfig, new StreamConfig("h264", 1920, 1080, 30, 8000), ProtocolJson.Default.StreamConfig, Now()));
await stream.WriteAsync(Message.Json(MessageType.Status, new Status(0.8, true, "nominal", 30, 8000, 0), ProtocolJson.Default.Status, Now()));

// Answer pings and print controls in the background.
_ = Task.Run(async () =>
{
    try
    {
        while (await stream.ReadAsync() is { } message)
        {
            if (message.Type == MessageType.Ping)
                await stream.WriteAsync(Message.Pong(message.Header.Timestamp, Now()));
            else if (message.Type == MessageType.Control)
                Console.WriteLine($"Control: {System.Text.Encoding.UTF8.GetString(message.Payload)}");
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
    var keyframe = i % 60 == 0;
    var payload = new byte[keyframe ? 120_000 : 30_000];
    random.NextBytes(payload);
    byte[] prefix = keyframe ? [0, 0, 0, 1, 0x67, 0, 0, 0, 1, 0x68, 0, 0, 0, 1, 0x65] : [0, 0, 0, 1, 0x41];
    prefix.CopyTo(payload, 0);
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

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

using System.Text.Json.Serialization;

namespace HitCam.Core.Protocol;

public static class ProtocolInfo
{
    public const int Version = 1;
    public const int DefaultPort = 47800;
    public const string BonjourServiceType = "_hitcam._tcp";
    public const string UriScheme = "hitcam";
}

public static class HelloStatus
{
    public const string Accepted = "accepted";
    public const string PairingRequired = "pairingRequired";
    public const string Busy = "busy";
    public const string VersionMismatch = "versionMismatch";
    /// <summary>Too many wrong PINs recently; the phone should retry later.</summary>
    public const string PairingLocked = "pairingLocked";
}

public sealed record Hello(
    int ProtocolVersion,
    string DeviceId,
    string DeviceName,
    string? Model = null,
    string? AppVersion = null,
    string? Token = null);

public sealed record HelloAck(int ProtocolVersion, string Status, string ServerName, string ServerId);

public sealed record PairRequest(string Pin);

public sealed record PairResult(bool Ok, string? Token, int AttemptsLeft);

public sealed record StreamConfig(string Codec, int Width, int Height, int Fps, int BitrateKbps);

public sealed record CameraInfo(
    string Id,
    string Name,
    string Position,
    double MinZoom,
    double MaxZoom,
    bool HasTorch,
    bool SupportsFocus);

public sealed record VideoPreset(int Width, int Height, int[] Fps);

public sealed record Capabilities(CameraInfo[] Cameras, VideoPreset[] Presets);

public sealed record NormalizedPoint(double X, double Y);

public sealed record CameraState(
    string CameraId,
    double Zoom,
    bool Torch,
    string FocusMode,
    double LensPosition,
    double ExposureBias,
    bool Mirror,
    int Rotation,
    int Width,
    int Height,
    int Fps,
    int BitrateKbps);

/// <summary>Camera control request; only non-null fields are applied by the phone.</summary>
public sealed record Control
{
    public string? CameraId { get; init; }
    public double? Zoom { get; init; }
    public bool? Torch { get; init; }
    public string? FocusMode { get; init; }
    public double? LensPosition { get; init; }
    public NormalizedPoint? FocusPoint { get; init; }
    public double? ExposureBias { get; init; }
    public bool? Mirror { get; init; }
    public int? Rotation { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Fps { get; init; }
    public int? BitrateKbps { get; init; }
}

public sealed record Status(
    double Battery,
    bool Charging,
    string Thermal,
    double Fps,
    int BitrateKbps,
    long DroppedFrames);

public sealed record Bye(string? Reason);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Hello))]
[JsonSerializable(typeof(HelloAck))]
[JsonSerializable(typeof(PairRequest))]
[JsonSerializable(typeof(PairResult))]
[JsonSerializable(typeof(StreamConfig))]
[JsonSerializable(typeof(Capabilities))]
[JsonSerializable(typeof(CameraState))]
[JsonSerializable(typeof(Control))]
[JsonSerializable(typeof(Status))]
[JsonSerializable(typeof(Bye))]
public sealed partial class ProtocolJson : JsonSerializerContext;

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

/// <param name="SupportsWhiteBalance">White balance can be locked to a temperature/tint (null: older phone app).</param>
/// <param name="SupportsExposureLock">Exposure can be locked (null: older phone app).</param>
public sealed record CameraInfo(
    string Id,
    string Name,
    string Position,
    double MinZoom,
    double MaxZoom,
    bool HasTorch,
    bool SupportsFocus,
    bool? SupportsWhiteBalance = null,
    bool? SupportsExposureLock = null);

public static class WhiteBalanceModes
{
    public const string Auto = "auto";
    public const string Locked = "locked";
}

public static class ExposureModes
{
    public const string Auto = "auto";
    public const string Locked = "locked";
}

public static class StabilizationModes
{
    public const string Off = "off";
    public const string Standard = "standard";
    public const string Cinematic = "cinematic";
}

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
    int BitrateKbps,
    // Added in app 0.2; null from older phone apps.
    string? WhiteBalanceMode = null,
    double? WhiteBalanceTemperature = null,
    double? WhiteBalanceTint = null,
    string? ExposureMode = null,
    string? Stabilization = null,
    string[]? StabilizationModes = null);

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
    /// <summary>"locked" with a temperature/tint (either may be omitted: the current value is kept), or "auto".</summary>
    public string? WhiteBalanceMode { get; init; }
    public double? WhiteBalanceTemperature { get; init; }
    public double? WhiteBalanceTint { get; init; }
    public string? ExposureMode { get; init; }
    public string? Stabilization { get; init; }
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

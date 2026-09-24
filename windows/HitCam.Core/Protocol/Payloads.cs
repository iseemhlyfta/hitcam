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

// Token is omitted from the JSON when null, so readers must treat it as optional.
public sealed record PairResult(bool Ok, string? Token = null, int AttemptsLeft = 0);

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

public sealed record VideoPreset(int Width, int Height, int[] Fps)
{
    public const int MaxFps = 8;
}

public sealed record Capabilities(CameraInfo[] Cameras, VideoPreset[] Presets) : IValidatedPayload
{
    public const int MaxCameras = 16;
    public const int MaxPresets = 16;

    void IValidatedPayload.Validate()
    {
        PayloadChecks.Array(Cameras, MaxCameras, "cameras");
        PayloadChecks.Array(Presets, MaxPresets, "presets");
        foreach (var preset in Presets)
            PayloadChecks.Array(preset.Fps, VideoPreset.MaxFps, "presets.fps");
    }
}

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
    string[]? StabilizationModes = null) : IValidatedPayload
{
    public const int MaxStabilizationModes = 8;

    void IValidatedPayload.Validate()
    {
        if (StabilizationModes is not null)
            PayloadChecks.Array(StabilizationModes, MaxStabilizationModes, "stabilizationModes");
    }
}

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

public sealed record Bye(string? Reason = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    // The phone is untrusted: a missing or null non-nullable field is a protocol error, not a crash later.
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
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

/// <summary>A payload with limits that deserialization alone cannot enforce; checked by <see cref="Message.ReadJson{T}"/>.</summary>
internal interface IValidatedPayload
{
    /// <exception cref="ProtocolException">The payload is out of bounds.</exception>
    void Validate();
}

internal static class PayloadChecks
{
    /// <summary>Nullable annotations do not cover array elements, and the counts bound the work the PC does per message.</summary>
    public static void Array<T>(T[] items, int max, string name)
    {
        if (items.Length > max)
            throw new ProtocolException($"{name} has {items.Length} entries, at most {max} are allowed.");
        foreach (var item in items)
        {
            if (item is null)
                throw new ProtocolException($"{name} contains null.");
        }
    }
}

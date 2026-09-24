using System.Text.Json;
using System.Text.Json.Serialization;

namespace HitCam.Desktop.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HitCam");

    public static string PairedDevices => Path.Combine(DataDirectory, "devices.json");

    public static string Settings => Path.Combine(DataDirectory, "settings.json");
}

public sealed record AppSettings
{
    /// <summary>Stable identity of this PC, so phones can recognize it across restarts.</summary>
    // Plain setters, not init: the JSON source generator fills init-only properties through an object
    // initializer and would overwrite these defaults with zeros for fields an older file does not have.
    public string ServerId { get; set; } = Guid.NewGuid().ToString();
    public int Port { get; set; } = Core.Protocol.ProtocolInfo.DefaultPort;
    /// <summary>NVIDIA AI noise removal (PC-side, RTX only).</summary>
    public bool DenoiseEnabled { get; set; }
    /// <summary>"fast", "general" or "maximum" (see <see cref="Services.DenoiseMode"/>); unknown values mean "general".</summary>
    public string DenoiseMode { get; set; } = "general";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings) && Parse(File.ReadAllBytes(AppPaths.Settings)) is { } settings)
                return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to defaults; they are written back below.
        }

        var created = new AppSettings();
        created.Save();
        return created;
    }

    /// <summary>Settings written by any earlier version; fields it did not know keep their defaults.</summary>
    public static AppSettings? Parse(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllBytes(AppPaths.Settings, JsonSerializer.SerializeToUtf8Bytes(this, SettingsJson.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; the app keeps working with in-memory values.
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

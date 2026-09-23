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
    public string ServerId { get; init; } = Guid.NewGuid().ToString();
    public int Port { get; init; } = Core.Protocol.ProtocolInfo.DefaultPort;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
            {
                var settings = JsonSerializer.Deserialize(File.ReadAllBytes(AppPaths.Settings), SettingsJson.Default.AppSettings);
                if (settings is not null)
                    return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to defaults; they are written back below.
        }

        var created = new AppSettings();
        created.Save();
        return created;
    }

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

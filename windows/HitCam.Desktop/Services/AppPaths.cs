using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HitCam.Desktop.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HitCam");

    public static string PairedDevices => Path.Combine(DataDirectory, "devices.json");

    public static string Settings => Path.Combine(DataDirectory, "settings.json");
}

public sealed partial record AppSettings
{
    /// <summary>Stable identity of this PC, so phones can recognize it across restarts.</summary>
    // Plain setters, not init: the JSON source generator fills init-only properties through an object
    // initializer and would overwrite these defaults with zeros for fields an older file does not have.
    public string ServerId { get; set; } = Guid.NewGuid().ToString();
    public int Port { get; set; } = Core.Protocol.ProtocolInfo.DefaultPort;
    // Up to 0.2.3 there were also "denoiseEnabled" and "denoiseMode" (NVIDIA AI noise removal, removed): they are
    // ignored when read and dropped on the next save.

    /// <summary>Picture processing on this PC; everything off or neutral by default.</summary>
    public ProcessingSettings Processing
    {
        get => _processing;
        // "processing": null in a hand-edited file means defaults.
        set => _processing = value ?? new ProcessingSettings();
    }

    private ProcessingSettings _processing = new();

    /// <summary>
    /// The master switch of the experimental features (NVIDIA noise removal, object analysis, hands, faces). Off: none of
    /// them runs, but their own settings are kept, so switching back on restores what was on.
    /// </summary>
    public bool Experiments { get; set; } = true;

    /// <summary>Object analysis; off by default.</summary>
    public VisionSettings Vision
    {
        get => _vision;
        set => _vision = value ?? new VisionSettings();
    }

    private VisionSettings _vision = new();

    /// <summary>Hand tracking; off by default.</summary>
    public HandSettings Hands
    {
        get => _hands;
        set => _hands = value ?? new HandSettings();
    }

    private HandSettings _hands = new();

    /// <summary>Face hiding; off by default.</summary>
    public FaceSettings Faces
    {
        get => _faces;
        set => _faces = value ?? new FaceSettings();
    }

    private FaceSettings _faces = new();

    public static AppSettings Load() => Load(AppPaths.Settings);

    /// <summary>
    /// Reads the settings, creating the file on first start. A damaged file is kept as <c>.bak</c> and replaced by
    /// defaults, but with the old <see cref="ServerId"/> if it can still be read: a new one would make every paired
    /// phone treat this PC as a stranger.
    /// </summary>
    public static AppSettings Load(string path)
    {
        byte[] json;
        try
        {
            if (!File.Exists(path))
            {
                var created = new AppSettings();
                created.Save(path);
                return created;
            }
            json = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable right now (e.g. locked): run with defaults and leave the file alone.
            return new AppSettings();
        }

        try
        {
            if (Parse(json) is { } settings)
                return settings;
        }
        catch (JsonException)
        {
            // Damaged; recovered below.
        }

        var recovered = new AppSettings();
        if (RecoverServerId(json) is { } serverId)
            recovered.ServerId = serverId;
        try
        {
            File.Move(path, path + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Overwritten below then; the identity is kept anyway if it was readable.
        }
        recovered.Save(path);
        return recovered;
    }

    /// <summary>Settings written by any earlier version; fields it did not know keep their defaults.</summary>
    public static AppSettings? Parse(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings);

    /// <summary>The server id from a file that no longer parses as a whole (e.g. cut short).</summary>
    public static string? RecoverServerId(ReadOnlySpan<byte> json) =>
        ServerIdPattern().Match(Encoding.UTF8.GetString(json)) is { Success: true } match ? match.Groups[1].Value : null;

    public void Save() => Save(AppPaths.Settings);

    /// <summary>Writes a temporary file and moves it over the old one, so a crash never leaves half a file.</summary>
    public void Save(string path)
    {
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(this, SettingsJson.Default.AppSettings));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; the app keeps working with in-memory values.
        }
    }

    [GeneratedRegex("""
        "serverId"\s*:\s*"([^"\\\s]{1,100})"
        """, RegexOptions.IgnoreCase)]
    private static partial Regex ServerIdPattern();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

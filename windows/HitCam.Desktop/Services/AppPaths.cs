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
    public int Port
    {
        get => _port;
        // Out of range (a hand-edited file) would make the server throw at every start.
        set => _port = value is >= 1 and <= 65535 ? value : Core.Protocol.ProtocolInfo.DefaultPort;
    }

    private int _port = Core.Protocol.ProtocolInfo.DefaultPort;

    // Set when the file exists but could not be read: saving these defaults over it would lose every setting and the
    // identity paired phones know this PC by. Copied along by "with".
    private bool _unsaved;
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

    /// <summary>Which features on the experiments tab are expanded; all collapsed by default.</summary>
    public ExperimentSections Sections
    {
        get => _sections;
        set => _sections = value ?? new ExperimentSections();
    }

    private ExperimentSections _sections = new();

    public static AppSettings Load() => Load(AppPaths.Settings);

    /// <summary>
    /// Reads the settings, creating the file on first start. A damaged file is kept as <c>.bak</c> and replaced by
    /// defaults, but with the old <see cref="ServerId"/> if it can still be read: a new one would make every paired
    /// phone treat this PC as a stranger.
    /// </summary>
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            var created = new AppSettings();
            created.Save(path);
            return created;
        }
        // Unreadable (held by an antivirus or a sync tool, no access): run with defaults and leave the file alone.
        if (ReadAll(path) is not { } json)
            return new AppSettings { _unsaved = true };

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

    /// <summary>The file's bytes, retried briefly while another process holds it; null if it stays unreadable.</summary>
    private static byte[]? ReadAll(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Settings written by any earlier version; fields it did not know keep their defaults.</summary>
    public static AppSettings? Parse(ReadOnlySpan<byte> json) =>
        // A UTF-8 byte order mark (Windows PowerShell 5.1, old Notepad) is not valid JSON for the reader.
        JsonSerializer.Deserialize(json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? json[3..] : json, SettingsJson.Default.AppSettings);

    /// <summary>The server id from a file that no longer parses as a whole (e.g. cut short).</summary>
    public static string? RecoverServerId(ReadOnlySpan<byte> json) =>
        ServerIdPattern().Match(Encoding.UTF8.GetString(json)) is { Success: true } match ? match.Groups[1].Value : null;

    public void Save() => Save(AppPaths.Settings);

    /// <summary>Writes a temporary file and moves it over the old one, so a crash never leaves half a file.</summary>
    public void Save(string path)
    {
        if (_unsaved)
            return;
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

/// <summary>Expanded feature sections (experiments tab, picture enhancement; "sections" in settings.json).</summary>
public sealed record ExperimentSections
{
    public bool Artifact { get; set; }
    public bool Vision { get; set; }
    public bool Hands { get; set; }
    public bool Faces { get; set; }

    /// <summary>"Picture enhancement" on the camera tab.</summary>
    public bool Enhance { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

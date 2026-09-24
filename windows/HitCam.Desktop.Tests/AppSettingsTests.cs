using HitCam.Desktop.Services;

namespace HitCam.Desktop.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HitCam.Tests." + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Settings_from_an_older_version_keep_the_defaults_of_new_fields()
    {
        // Written by 0.1, before noise removal existed.
        var settings = AppSettings.Parse("""{"serverId":"b929fd73","port":47800}"""u8);

        Assert.NotNull(settings);
        Assert.Equal("b929fd73", settings.ServerId);
        Assert.False(settings.DenoiseEnabled);
        Assert.Equal("general", settings.DenoiseMode);
    }

    [Fact]
    public void Missing_identity_and_port_get_defaults_too()
    {
        var settings = AppSettings.Parse("{}"u8);

        Assert.NotNull(settings);
        Assert.False(string.IsNullOrEmpty(settings.ServerId));
        Assert.Equal(47800, settings.Port);
    }

    [Fact]
    public void First_start_creates_the_file_and_later_starts_read_it_back()
    {
        var created = AppSettings.Load(SettingsPath);
        Assert.True(File.Exists(SettingsPath));

        (created with { DenoiseEnabled = true, DenoiseMode = "maximum" }).Save(SettingsPath);
        var loaded = AppSettings.Load(SettingsPath);

        Assert.Equal(created.ServerId, loaded.ServerId);
        Assert.True(loaded.DenoiseEnabled);
        Assert.Equal("maximum", loaded.DenoiseMode);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void A_damaged_file_is_kept_as_backup_and_the_identity_survives()
    {
        Directory.CreateDirectory(_directory);
        // Cut short in the middle of a write.
        const string damaged = "{\n  \"serverId\": \"b929fd73-5d5e-4f7a-9d3c-0a4b6f1e2d3c\",\n  \"port\": 478";
        File.WriteAllText(SettingsPath, damaged);

        var settings = AppSettings.Load(SettingsPath);

        Assert.Equal("b929fd73-5d5e-4f7a-9d3c-0a4b6f1e2d3c", settings.ServerId);
        Assert.Equal(damaged, File.ReadAllText(SettingsPath + ".bak"));
        Assert.Equal(settings.ServerId, AppSettings.Load(SettingsPath).ServerId);
    }

    [Fact]
    public void Garbage_gets_a_new_identity_but_the_backup_stays()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(SettingsPath, [0, 0, 0, 0]);

        var settings = AppSettings.Load(SettingsPath);

        Assert.False(string.IsNullOrEmpty(settings.ServerId));
        Assert.Equal([0, 0, 0, 0], File.ReadAllBytes(SettingsPath + ".bak"));
        Assert.Equal(settings.ServerId, AppSettings.Parse(File.ReadAllBytes(SettingsPath))?.ServerId);
    }
}

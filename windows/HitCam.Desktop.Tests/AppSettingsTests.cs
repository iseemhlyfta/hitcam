using HitCam.Desktop.Services;

namespace HitCam.Desktop.Tests;

public sealed class AppSettingsTests
{
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
}

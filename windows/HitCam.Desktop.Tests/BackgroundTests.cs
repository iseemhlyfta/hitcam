using System.Reactive.Concurrency;
using System.Runtime.InteropServices;
using HitCam.Desktop.Services;
using HitCam.Desktop.ViewModels;

namespace HitCam.Desktop.Tests;

public sealed class BackgroundTests
{
    private readonly List<BackgroundSettings> _applied = [];
    private readonly List<BackgroundSettings> _saved = [];

    private BackgroundViewModel Create(BackgroundSettings? initial = null, bool modelFound = true) =>
        new(initial ?? new BackgroundSettings(), () => modelFound, _applied.Add, _saved.Add, ImmediateScheduler.Instance);

    [Fact]
    public void The_struct_matches_the_dll() => Assert.Equal(24, Marshal.SizeOf<HitCamBackground>());

    [Fact]
    public void Settings_default_to_off_blur_and_blurring_until_the_person_is_found()
    {
        var background = AppSettings.Parse("""{"serverId":"a"}"""u8)!.Background;

        Assert.False(background.Enabled);
        Assert.Equal(BackgroundMode.Blur, background.Mode);
        Assert.Equal(BackgroundSettings.DefaultStrength, background.Strength);
        Assert.True(background.BlurWhileUnknown);
        Assert.Null(background.ImagePath);
        Assert.Equal(new BackgroundSettings(), AppSettings.Parse("""{"background":null}"""u8)!.Background);
        Assert.Equal(BackgroundMode.Replace, AppSettings.Parse("""{"background":{"mode":"replace"}}"""u8)!.Background.Mode);
    }

    [Fact]
    public void Values_are_clamped_and_off_sends_mode_zero()
    {
        Assert.Equal(100, new BackgroundSettings { Strength = 500 }.Strength);
        Assert.Equal(0, new BackgroundSettings { Edge = -3 }.Edge);
        Assert.Equal(BackgroundMode.Blur, new BackgroundSettings { Mode = (BackgroundMode)7 }.Mode);
        Assert.Equal(0, new BackgroundSettings { Mode = BackgroundMode.Replace }.ToNative().Mode);
    }

    [Fact]
    public void On_the_dll_gets_the_mode_and_fractions()
    {
        var native = new BackgroundSettings { Enabled = true, Mode = BackgroundMode.Replace, Strength = 80, Edge = 20, BlurWhileUnknown = false }.ToNative();

        Assert.Equal(2, native.Mode);
        Assert.Equal(0.8f, native.Strength, 1e-5f);
        Assert.Equal(0.2f, native.Edge, 1e-5f);
        Assert.Equal(0, native.FailClosed);
        Assert.Equal(1, new BackgroundSettings { Enabled = true }.ToNative().Mode);
    }

    [Fact]
    public void Changes_are_applied_at_once_and_saved()
    {
        var background = Create();

        background.IsOn = true;
        background.SelectedMode = background.Modes[1];
        background.Strength = 42.4;

        Assert.True(_applied[^1].Enabled);
        Assert.Equal(BackgroundMode.Replace, _applied[^1].Mode);
        Assert.Equal(42, _applied[^1].Strength);
        Assert.Equal(_applied[^1], _saved[^1]);
        Assert.True(background.IsReplace);
    }

    [Fact]
    public void The_picture_shows_by_name_and_the_strength_only_while_there_is_blur()
    {
        var background = Create(new BackgroundSettings { Mode = BackgroundMode.Replace });
        Assert.Equal(Loc.BackgroundNoImage, background.ImageText);
        Assert.True(background.ShowsStrength);   // no picture yet: the room is blurred

        background.SetImage(@"C:\Pictures\beach.jpg");

        Assert.Equal("beach.jpg", background.ImageText);
        Assert.False(background.ShowsStrength);
        Assert.Equal(@"C:\Pictures\beach.jpg", _applied[^1].ImagePath);
    }

    [Fact]
    public void A_missing_model_is_reported_when_switched_on()
    {
        var background = Create(modelFound: false);
        background.IsOn = true;
        Assert.True(background.HasNoModel);
    }

    [Fact]
    public void A_picture_that_is_not_one_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"HitCam.Tests.{Guid.NewGuid():N}.png");
        File.WriteAllText(path, "not a picture");
        try
        {
            Assert.Null(BackgroundImage.Load(path));
            Assert.Null(BackgroundImage.Load(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_picture_is_read_as_packed_bgra_and_large_ones_are_scaled_down()
    {
        var path = Path.Combine(Path.GetTempPath(), $"HitCam.Tests.{Guid.NewGuid():N}.png");
        using (var bitmap = new SkiaSharp.SKBitmap(5000, 100))
        {
            bitmap.Erase(new SkiaSharp.SKColor(255, 0, 0));
            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        try
        {
            var image = BackgroundImage.Load(path);

            Assert.NotNull(image);
            Assert.Equal(BackgroundImage.MaxWidth, image.Value.Width);
            Assert.Equal(image.Value.Width * image.Value.Height * 4, image.Value.Pixels.Length);
            Assert.Equal([0, 0, 255, 255], image.Value.Pixels[..4]);   // BGRA red
        }
        finally
        {
            File.Delete(path);
        }
    }
}

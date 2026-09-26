using System.Reactive.Concurrency;
using System.Runtime.InteropServices;
using HitCam.Desktop.Services;
using HitCam.Desktop.ViewModels;

namespace HitCam.Desktop.Tests;

public sealed class ProcessingTests
{
    private readonly List<HitCamProcessing> _applied = [];
    private readonly List<ProcessingSettings> _saved = [];
    // Virtual time: saving waits for the user to stop dragging.
    private readonly HistoricalScheduler _time = new();

    private ProcessingViewModel Create(ProcessingSettings? initial = null) =>
        new(initial ?? new ProcessingSettings(), _applied.Add, _saved.Add, _time);

    [Fact]
    public void The_struct_matches_the_native_layout()
    {
        Assert.Equal(32, Marshal.SizeOf<HitCamProcessing>());
        Assert.Equal(0, (int)Marshal.OffsetOf<HitCamProcessing>(nameof(HitCamProcessing.TemporalStrength)));
        Assert.Equal(4, (int)Marshal.OffsetOf<HitCamProcessing>(nameof(HitCamProcessing.ArtifactReduction)));
        Assert.Equal(8, (int)Marshal.OffsetOf<HitCamProcessing>(nameof(HitCamProcessing.Brightness)));
        Assert.Equal(20, (int)Marshal.OffsetOf<HitCamProcessing>(nameof(HitCamProcessing.Sharpness)));
        Assert.Equal(28, (int)Marshal.OffsetOf<HitCamProcessing>(nameof(HitCamProcessing.Highlights)));
    }

    [Fact]
    public void Slider_units_map_to_the_native_ranges()
    {
        var native = new ProcessingSettings
        {
            TemporalStrength = 70, ArtifactReduction = 1, Brightness = -100, Contrast = 50, Saturation = 100,
            Shadows = 25, Highlights = -40, Sharpness = 30,
        }.ToNative();

        Assert.Equal(70, native.TemporalStrength);
        Assert.Equal(1, native.ArtifactReduction);
        Assert.Equal(-1f, native.Brightness);
        Assert.Equal(0.5f, native.Contrast);
        Assert.Equal(1f, native.Saturation);
        Assert.Equal(0.25f, native.Shadows);
        Assert.Equal(-0.4f, native.Highlights);
        Assert.Equal(0.3f, native.Sharpness);
    }

    [Fact]
    public void Defaults_are_all_off_and_out_of_range_values_are_clamped()
    {
        Assert.Equal(default, new ProcessingSettings().ToNative());

        var native = new ProcessingSettings
        {
            TemporalStrength = 500, ArtifactReduction = 7, Brightness = -300, Sharpness = -5, Highlights = 101,
        }.ToNative();

        Assert.Equal((100, 2, -1f, 0f, 1f), (native.TemporalStrength, native.ArtifactReduction, native.Brightness, native.Sharpness, native.Highlights));
    }

    [Fact]
    public void Saved_settings_are_applied_at_once_and_each_change_immediately()
    {
        var processing = Create(new ProcessingSettings { TemporalStrength = 30 });
        Assert.Equal(30, Assert.Single(_applied).TemporalStrength);

        processing.Brightness = 20;
        processing.Sharpness = 55.4;

        Assert.Equal(3, _applied.Count);
        Assert.Equal(0.2f, _applied[^1].Brightness);
        Assert.Equal(0.55f, _applied[^1].Sharpness);
        Assert.Equal("+20", processing.BrightnessText);
        Assert.Equal("55", processing.SharpnessText);
    }

    [Fact]
    public void Settings_are_saved_once_the_slider_stops()
    {
        var processing = Create();
        for (var value = 1; value <= 40; value++)
        {
            processing.TemporalStrength = value;
            _time.AdvanceBy(TimeSpan.FromMilliseconds(30));
        }
        Assert.Empty(_saved);

        _time.AdvanceBy(TimeSpan.FromSeconds(1));

        Assert.Equal(40, Assert.Single(_saved).TemporalStrength);
    }

    [Fact]
    public void A_change_not_yet_saved_is_written_on_exit()
    {
        var processing = Create();
        processing.SaveNow();
        Assert.Empty(_saved);

        processing.Contrast = -30;
        processing.SaveNow();

        Assert.Equal(-30, Assert.Single(_saved).Contrast);
        _time.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Single(_saved);
    }

    [Fact]
    public void Reset_returns_colour_and_sharpness_to_neutral_but_keeps_noise_reduction()
    {
        var processing = Create(new ProcessingSettings { TemporalStrength = 50, Brightness = 10, Shadows = -20, Sharpness = 40 });
        Assert.True(processing.IsColorAdjusted);

        processing.ResetColorCommand.Execute().Subscribe();

        Assert.False(processing.IsColorAdjusted);
        Assert.Equal(new ProcessingSettings { TemporalStrength = 50 }, processing.Settings);
        Assert.Equal("0", processing.ShadowsText);
        Assert.Equal((50, 0f, 0f), (_applied[^1].TemporalStrength, _applied[^1].Brightness, _applied[^1].Sharpness));
    }

    [Fact]
    public void Artifact_reduction_reaches_the_dll_only_once_the_gpu_check_passed()
    {
        var processing = Create(new ProcessingSettings { ArtifactReduction = ProcessingSettings.ArtifactReductionStrong });
        Assert.Equal(0, _applied[^1].ArtifactReduction);
        Assert.Equal(ProcessingSettings.ArtifactReductionStrong, processing.SelectedArtifactReduction.Level);

        processing.IsArtifactReductionAvailable = true;
        Assert.Equal(2, _applied[^1].ArtifactReduction);

        processing.SelectedArtifactReduction = processing.ArtifactReductionOptions[0];
        Assert.Equal(1, _applied[^1].ArtifactReduction);
        _time.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Assert.Single(_saved).ArtifactReduction);
    }

    [Fact]
    public void The_artifact_switch_comes_back_with_the_last_strength()
    {
        var processing = Create(new ProcessingSettings());
        processing.IsArtifactReductionAvailable = true;
        Assert.False(processing.IsArtifactReductionOn);
        Assert.Equal(ProcessingSettings.ArtifactReductionGentle, processing.SelectedArtifactReduction.Level);

        processing.IsArtifactReductionOn = true;
        Assert.Equal(1, _applied[^1].ArtifactReduction);
        processing.SelectedArtifactReduction = processing.ArtifactReductionOptions[1];
        Assert.Equal(2, _applied[^1].ArtifactReduction);

        processing.IsArtifactReductionOn = false;
        Assert.Equal(0, _applied[^1].ArtifactReduction);
        Assert.Equal(ProcessingSettings.ArtifactReductionStrong, processing.SelectedArtifactReduction.Level);
        processing.IsArtifactReductionOn = true;
        Assert.Equal(2, _applied[^1].ArtifactReduction);
    }

    [Fact]
    public void Stats_show_the_gpu_time_and_artifact_time_and_error_only_when_it_is_on()
    {
        var processing = Create(new ProcessingSettings { ArtifactReduction = ProcessingSettings.ArtifactReductionGentle });

        processing.ShowStats(new ProcessingStats(null, null, 0));
        Assert.False(processing.HasStats);

        processing.ShowStats(new ProcessingStats(1.25, 4.5, 3));
        Assert.Contains("1", processing.StatsText);
        Assert.DoesNotContain("4", processing.StatsText);
        Assert.False(processing.HasArtifactError);

        processing.IsArtifactReductionAvailable = true;
        processing.ShowStats(new ProcessingStats(1.25, 4.5, 0));
        Assert.Contains("4", processing.StatsText);
        processing.ShowStats(new ProcessingStats(1.25, null, 3));
        Assert.True(processing.HasArtifactError);
        Assert.Contains("3", processing.ArtifactErrorText);

        processing.ClearStats();
        Assert.False(processing.HasStats || processing.HasArtifactError);
    }

    [Fact]
    public void Processing_switched_off_sends_neutral_settings_but_keeps_the_sliders()
    {
        var settings = new ProcessingSettings { Enabled = false, TemporalStrength = 52, Brightness = -23, Shadows = 40, ArtifactReduction = 1 };
        var native = settings.ToNative();

        Assert.Equal(0, native.TemporalStrength);
        Assert.Equal(0f, native.Brightness);
        Assert.Equal(0f, native.Shadows);
        Assert.Equal(1, native.ArtifactReduction);   // NVIDIA has its own switch (experiments)
        Assert.Equal(52, settings.TemporalStrength);
        Assert.True(AppSettings.Parse("""{"processing":{"temporalStrength":10}}"""u8)!.Processing.Enabled);
    }
}

using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using HitCam.Core.Protocol;
using HitCam.Desktop.ViewModels;

namespace HitCam.Desktop.Tests;

public sealed class CameraControlsTests
{
    private static readonly Capabilities Capabilities = new(
        [
            new CameraInfo("back-wide", "Wide", "back", 1, 10, true, true, true, true),
            new CameraInfo("back-tele", "Telephoto", "back", 1, 10, true, true, true, true),
            new CameraInfo("front", "Front", "front", 1, 5, false, false, true, true),
        ],
        [new VideoPreset(1280, 720, [30, 60]), new VideoPreset(1920, 1080, [30, 60])]);

    private static readonly CameraState Initial = new("back-wide", 1, false, "continuous", 0.5, 0, false, 0, 1920, 1080, 30, 8000,
        WhiteBalanceModes.Auto, 5200, 0, ExposureModes.Auto, StabilizationModes.Off,
        [StabilizationModes.Off, StabilizationModes.Standard, StabilizationModes.Cinematic]);

    private readonly ConcurrentQueue<Control> _sent = new();
    // Virtual time: throttling and the echo grace run only when a test advances the clock.
    private readonly HistoricalScheduler _time = new();
    private readonly CameraControlsViewModel _controls;

    public CameraControlsTests()
    {
        _controls = new CameraControlsViewModel(control =>
        {
            _sent.Enqueue(control);
            return Task.CompletedTask;
        }, _time);
    }

    [Fact]
    public void State_from_the_phone_fills_the_controls_without_echoing_back()
    {
        _controls.ApplyCapabilities(Capabilities);
        _controls.ApplyState(Initial with { CameraId = "back-tele", Zoom = 2.5, Fps = 60, Mirror = true, Rotation = 90 });

        Assert.True(_controls.IsAvailable);
        Assert.Equal("back-tele", _controls.SelectedCamera?.Id);
        Assert.Equal(new QualityOption(1920, 1080, 60, 12000), _controls.SelectedQuality);
        Assert.Equal(2.5, _controls.Zoom);
        Assert.True(_controls.Mirror);
        Assert.Equal("90°", _controls.RotationText);
        Assert.Equal(4, _controls.Qualities.Count);

        _time.AdvanceBy(TimeSpan.FromMilliseconds(300));
        Assert.Empty(_sent);
    }

    [Fact]
    public void State_that_arrives_before_capabilities_is_applied_once_they_come()
    {
        _controls.ApplyState(Initial with { CameraId = "front" });
        Assert.False(_controls.IsAvailable);

        _controls.ApplyCapabilities(Capabilities);

        Assert.True(_controls.IsAvailable);
        Assert.Equal("front", _controls.SelectedCamera?.Id);
        Assert.False(_controls.HasTorch);
        Assert.Equal(5, _controls.MaxZoom);
    }

    [Fact]
    public void Dragging_a_slider_sends_one_throttled_control()
    {
        Ready();

        _controls.Zoom = 2;
        _controls.Zoom = 3;
        _controls.Zoom = 4;
        _time.AdvanceBy(TimeSpan.FromMilliseconds(300));

        var control = Assert.Single(_sent);
        Assert.Equal(4, control.Zoom);
        Assert.Null(control.CameraId);
    }

    [Fact]
    public void Choosing_lens_quality_and_toggles_sends_controls_immediately()
    {
        Ready();

        _controls.SelectedCamera = _controls.Cameras.Single(c => c.Id == "back-tele");
        _controls.SelectedQuality = _controls.Qualities.Single(q => q is { Height: 720, Fps: 60 });
        _controls.Torch = true;
        _controls.Mirror = true;
        _controls.RotateCommand.Execute().Subscribe();

        var sent = _sent.ToArray();
        Assert.Equal(5, sent.Length);
        Assert.Equal("back-tele", sent[0].CameraId);
        Assert.Equal((1280, 720, 60, 6000), (sent[1].Width, sent[1].Height, sent[1].Fps, sent[1].BitrateKbps));
        Assert.True(sent[2].Torch);
        Assert.True(sent[3].Mirror);
        Assert.Equal(90, sent[4].Rotation);
    }

    [Fact]
    public void Focus_slider_locks_focus_and_autofocus_button_unlocks_it()
    {
        Ready();

        _controls.LensPosition = 0.8;
        _time.AdvanceBy(TimeSpan.FromMilliseconds(300));
        _controls.AutoFocusCommand.Execute().Subscribe();

        var sent = _sent.ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Equal(("locked", 0.8), (sent[0].FocusMode, sent[0].LensPosition));
        Assert.Equal("continuous", sent[1].FocusMode);
    }

    [Fact]
    public void A_stale_echo_does_not_snap_the_slider_back_but_the_phone_has_the_last_word()
    {
        Ready();

        _controls.Zoom = 4;
        // The phone's answer to an earlier state arrives while the user is still dragging.
        _controls.ApplyState(Initial with { Zoom = 1.5 });
        Assert.Equal(4, _controls.Zoom);

        // Once editing stops, the newest state from the phone is shown (here: it clamped the zoom).
        _controls.ApplyState(Initial with { Zoom = 3 });
        _time.AdvanceBy(TimeSpan.FromMilliseconds(1200));
        Assert.Equal(3, _controls.Zoom);
    }

    [Fact]
    public void Every_edit_restarts_the_grace_period()
    {
        Ready();

        _controls.Zoom = 4;
        _time.AdvanceBy(TimeSpan.FromMilliseconds(600));
        _controls.Zoom = 5;
        _controls.ApplyState(Initial with { Zoom = 2 });
        _time.AdvanceBy(TimeSpan.FromMilliseconds(600));
        Assert.Equal(5, _controls.Zoom);

        _time.AdvanceBy(TimeSpan.FromMilliseconds(200));
        Assert.Equal(2, _controls.Zoom);
        Assert.Equal([4.0, 5.0], _sent.Select(c => c.Zoom ?? 0));
    }

    [Fact]
    public void White_balance_sliders_lock_it_at_the_chosen_temperature_and_tint()
    {
        Ready();
        Assert.True(_controls.SupportsWhiteBalance);
        Assert.True(_controls.IsAutoWhiteBalance);

        _controls.WhiteBalanceTemperature = 3000;
        _controls.WhiteBalanceTemperature = 3400.4;
        _controls.WhiteBalanceTint = -12;
        _time.AdvanceBy(TimeSpan.FromMilliseconds(300));

        var control = Assert.Single(_sent);
        Assert.Equal((WhiteBalanceModes.Locked, 3400.0, -12.0), (control.WhiteBalanceMode, control.WhiteBalanceTemperature, control.WhiteBalanceTint));
        Assert.Equal("3400 K", _controls.WhiteBalanceText);
    }

    [Fact]
    public void Auto_white_balance_button_returns_to_auto_and_the_phone_state_is_mirrored()
    {
        Ready();
        _controls.ApplyState(Initial with { WhiteBalanceMode = WhiteBalanceModes.Locked, WhiteBalanceTemperature = 4100 });
        Assert.False(_controls.IsAutoWhiteBalance);
        Assert.Equal(4100, _controls.WhiteBalanceTemperature);
        Assert.Empty(_sent);

        _controls.AutoWhiteBalanceCommand.Execute().Subscribe();

        Assert.Equal(WhiteBalanceModes.Auto, Assert.Single(_sent).WhiteBalanceMode);
    }

    [Fact]
    public void Exposure_lock_is_sent_and_disables_the_bias_slider()
    {
        Ready();
        Assert.True(_controls.CanAdjustExposure);

        _controls.IsExposureLocked = true;
        Assert.False(_controls.CanAdjustExposure);
        _controls.IsExposureLocked = false;

        Assert.Equal([ExposureModes.Locked, ExposureModes.Auto], _sent.Select(c => c.ExposureMode));
    }

    [Fact]
    public void Stabilization_offers_only_the_modes_of_the_current_format()
    {
        Ready();
        Assert.True(_controls.SupportsStabilization);
        Assert.Equal(["off", "standard", "cinematic"], _controls.StabilizationOptions.Select(o => o.Id));

        _controls.SelectedStabilization = _controls.StabilizationOptions.Single(o => o.Id == StabilizationModes.Standard);
        Assert.Equal(StabilizationModes.Standard, Assert.Single(_sent).Stabilization);

        // e.g. 1080p60 on some phones: no stabilization at all.
        _controls.ApplyState(Initial with { Stabilization = StabilizationModes.Off, StabilizationModes = [StabilizationModes.Off] });
        // Right after the local choice the phone state is held back until editing settles.
        _time.AdvanceBy(TimeSpan.FromMilliseconds(1200));
        Assert.False(_controls.SupportsStabilization);
    }

    [Fact]
    public void Phone_noise_reduction_offers_only_the_camera_s_modes()
    {
        Ready();
        Assert.False(_controls.SupportsNoiseReduction);

        _controls.ApplyState(Initial with { NoiseReduction = NoiseReductionModes.Fast, NoiseReductionModes = [NoiseReductionModes.Off, NoiseReductionModes.Fast] });

        Assert.True(_controls.SupportsNoiseReduction);
        Assert.Equal(["off", "fast", "high"], _controls.NoiseReductionOptions.Select(o => o.Id));
        Assert.Equal([true, true, false], _controls.NoiseReductionOptions.Select(o => o.IsOffered));
        Assert.Equal(NoiseReductionModes.Fast, _controls.SelectedNoiseReduction?.Id);
        _time.AdvanceBy(TimeSpan.FromMilliseconds(300));
        Assert.Empty(_sent);

        // Not offered: nothing is sent and the selection stays.
        _controls.SelectedNoiseReduction = _controls.NoiseReductionOptions.Single(o => o.Id == NoiseReductionModes.High);
        Assert.Equal(NoiseReductionModes.Fast, _controls.SelectedNoiseReduction?.Id);
        Assert.Empty(_sent);

        _controls.SelectedNoiseReduction = _controls.NoiseReductionOptions.Single(o => o.Id == NoiseReductionModes.Off);
        Assert.Equal(NoiseReductionModes.Off, Assert.Single(_sent).NoiseReduction);
    }

    [Fact]
    public void Disconnect_hides_phone_noise_reduction()
    {
        Ready();
        _controls.ApplyState(Initial with { NoiseReduction = NoiseReductionModes.High, NoiseReductionModes = [NoiseReductionModes.Off, NoiseReductionModes.High] });
        Assert.True(_controls.SupportsNoiseReduction);

        _controls.Reset();

        Assert.False(_controls.SupportsNoiseReduction);
        Assert.Null(_controls.SelectedNoiseReduction);
    }

    [Fact]
    public void An_older_phone_app_shows_no_new_settings()
    {
        _controls.ApplyCapabilities(new Capabilities(
            [new CameraInfo("back-wide", "Wide", "back", 1, 10, true, true)],
            [new VideoPreset(1920, 1080, [30])]));
        _controls.ApplyState(new CameraState("back-wide", 1, false, "continuous", 0.5, 0, false, 0, 1920, 1080, 30, 8000));

        Assert.True(_controls.IsAvailable);
        Assert.False(_controls.SupportsWhiteBalance);
        Assert.False(_controls.SupportsExposureLock);
        Assert.False(_controls.SupportsStabilization);
        Assert.False(_controls.SupportsNoiseReduction);
    }

    [Fact]
    public void Disconnect_clears_the_controls()
    {
        Ready();

        _controls.Reset();

        Assert.False(_controls.IsAvailable);
        Assert.Empty(_controls.Cameras);
        Assert.Null(_controls.SelectedCamera);
    }

    private void Ready()
    {
        _controls.ApplyCapabilities(Capabilities);
        _controls.ApplyState(Initial);
        _sent.Clear();
    }
}

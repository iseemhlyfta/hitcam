using System.Collections.Concurrent;
using HitCam.Core.Protocol;
using HitCam.Desktop.ViewModels;

namespace HitCam.Desktop.Tests;

public sealed class CameraControlsTests
{
    private static readonly Capabilities Capabilities = new(
        [
            new CameraInfo("back-wide", "Wide", "back", 1, 10, true, true),
            new CameraInfo("back-tele", "Telephoto", "back", 1, 10, true, true),
            new CameraInfo("front", "Front", "front", 1, 5, false, false),
        ],
        [new VideoPreset(1280, 720, [30, 60]), new VideoPreset(1920, 1080, [30, 60])]);

    private static readonly CameraState Initial = new("back-wide", 1, false, "continuous", 0.5, 0, false, 0, 1920, 1080, 30, 8000);

    private readonly ConcurrentQueue<Control> _sent = new();
    private readonly CameraControlsViewModel _controls;

    public CameraControlsTests()
    {
        _controls = new CameraControlsViewModel(control =>
        {
            _sent.Enqueue(control);
            return Task.CompletedTask;
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task State_from_the_phone_fills_the_controls_without_echoing_back()
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

        await Task.Delay(300, Ct);
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
    public async Task Dragging_a_slider_sends_one_throttled_control()
    {
        Ready();

        _controls.Zoom = 2;
        _controls.Zoom = 3;
        _controls.Zoom = 4;
        await Task.Delay(300, Ct);

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
    public async Task Focus_slider_locks_focus_and_autofocus_button_unlocks_it()
    {
        Ready();

        _controls.LensPosition = 0.8;
        await Task.Delay(300, Ct);
        _controls.AutoFocusCommand.Execute().Subscribe();

        var sent = _sent.ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Equal(("locked", 0.8), (sent[0].FocusMode, sent[0].LensPosition));
        Assert.Equal("continuous", sent[1].FocusMode);
    }

    [Fact]
    public async Task A_stale_echo_does_not_snap_the_slider_back_but_the_phone_has_the_last_word()
    {
        Ready();

        _controls.Zoom = 4;
        // The phone's answer to an earlier state arrives while the user is still dragging.
        _controls.ApplyState(Initial with { Zoom = 1.5 });
        Assert.Equal(4, _controls.Zoom);

        // Once editing stops, the newest state from the phone is shown (here: it clamped the zoom).
        _controls.ApplyState(Initial with { Zoom = 3 });
        await Task.Delay(1200, Ct);
        Assert.Equal(3, _controls.Zoom);
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

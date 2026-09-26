using System.Reactive.Concurrency;
using System.Runtime.InteropServices;
using HitCam.Desktop.Services;
using HitCam.Desktop.ViewModels;

namespace HitCam.Desktop.Tests;

public sealed class FramingTests
{
    private readonly List<FramingSettings> _applied = [];
    private readonly List<FramingSettings> _saved = [];

    private FramingViewModel Create(FramingSettings? initial = null, bool modelsFound = true) =>
        new(initial ?? new FramingSettings(), () => modelsFound, _applied.Add, _saved.Add, ImmediateScheduler.Instance);

    [Fact]
    public void The_struct_matches_the_dll()
    {
        Assert.Equal(16, Marshal.SizeOf<HitCamFraming>());
        var framing = HitCamFraming.From(new System.Drawing.RectangleF(0.1f, 0.2f, 0.5f, 0.5f));
        Assert.Equal((0.1f, 0.2f, 0.6f, 0.7f), (framing.Left, framing.Top, framing.Right, framing.Bottom));
    }

    [Fact]
    public void Settings_default_to_off_and_the_zoom_is_clamped()
    {
        var framing = AppSettings.Parse("""{"serverId":"a"}"""u8)!.Framing;
        Assert.False(framing.Enabled);
        Assert.Equal(FramingSettings.DefaultMaxZoom, framing.MaxZoom);
        Assert.Equal(200, new FramingSettings { MaxZoom = 900 }.MaxZoom);
        Assert.Equal(110, new FramingSettings { MaxZoom = 0 }.MaxZoom);
    }

    [Fact]
    public void Changes_are_applied_saved_and_shown_as_a_factor()
    {
        var framing = Create();

        framing.IsOn = true;
        framing.MaxZoom = 147;

        Assert.True(_applied[^1].Enabled);
        Assert.Equal(150, _applied[^1].MaxZoom);
        Assert.Equal(_applied[^1], _saved[^1]);
        Assert.EndsWith("×", framing.MaxZoomText);
        Assert.StartsWith("1", framing.MaxZoomText);
    }

    [Fact]
    public void Missing_face_models_are_reported_when_switched_on()
    {
        var framing = Create(modelsFound: false);
        framing.IsOn = true;
        Assert.True(framing.HasNoModels);
    }
}

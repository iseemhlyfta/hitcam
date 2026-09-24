using System.Reactive.Concurrency;
using System.Runtime.InteropServices;
using HitCam.Desktop.Services;
using HitCam.Desktop.ViewModels;
using HitCam.Desktop.Views;
using HitCam.Vision;
using HitCam.Vision.Hands;
using PointF = System.Drawing.PointF;

namespace HitCam.Desktop.Tests;

public sealed class HandsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HitCam.Tests." + Guid.NewGuid().ToString("N"));
    private readonly HistoricalScheduler _time = new();
    private readonly List<HandSettings> _applied = [];
    private readonly List<HandSettings> _saved = [];
    private bool _modelsFound = true;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private HandsViewModel CreateViewModel(HandSettings? initial = null) =>
        new(initial ?? new HandSettings(), () => _modelsFound, _applied.Add, _saved.Add, _time);

    private static HandResult Result(int hands, int shots = 0) => new(
        [.. Enumerable.Range(1, hands).Select(id => new TrackedHand(id, new PointF[21], 0.9f, true))],
        new VisionStats(4.4, 29.6, "DirectML"), 1, 960, 540,
        [.. Enumerable.Range(1, shots).Select(id => new Shot(id, new PointF(0.5f, 0.5f), new PointF(1, 0), 0.2f))]);

    // Settings

    [Fact]
    public void Settings_default_to_off_without_lines()
    {
        var settings = AppSettings.Parse("""{"serverId":"a"}"""u8)!;

        Assert.False(settings.Hands.Enabled);
        Assert.False(settings.Hands.ShowSkeleton);
        Assert.True(settings.Hands.Shots);
        Assert.False(AppSettings.Parse("""{"hands":{"shots":false}}"""u8)!.Hands.Shots);
        Assert.Equal(new HandSettings(), AppSettings.Parse("""{"hands":null}"""u8)!.Hands);
        Assert.True(AppSettings.Parse("""{"hands":{"enabled":true}}"""u8)!.Hands.Enabled);
    }

    [Fact]
    public void Hand_settings_survive_a_save_and_load()
    {
        var path = Path.Combine(_directory, "settings.json");
        var hands = new HandSettings { Enabled = true, ShowSkeleton = true, Shots = false };

        (AppSettings.Load(path) with { Hands = hands }).Save(path);

        Assert.Equal(hands, AppSettings.Load(path).Hands);
        Assert.Contains("\"hands\"", File.ReadAllText(path));
    }

    // Panel

    [Fact]
    public void Switches_apply_at_once_and_save_after_a_pause()
    {
        var hands = CreateViewModel();

        hands.IsEnabled = true;
        hands.ShowSkeleton = true;

        Assert.Equal([new HandSettings { Enabled = true }, new HandSettings { Enabled = true, ShowSkeleton = true }], _applied);
        Assert.Empty(_saved);
        _time.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Equal([new HandSettings { Enabled = true, ShowSkeleton = true }], _saved);

        // No change, nothing applied or saved.
        hands.ShowSkeleton = true;
        _time.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _applied.Count);
        Assert.Single(_saved);
    }

    [Fact]
    public void Missing_models_are_looked_for_again_when_switched_on()
    {
        _modelsFound = false;
        var hands = CreateViewModel();
        Assert.True(hands.HasNoModels);
        Assert.Contains(HandModelFiles.SubDirectory, hands.NoModelsText);

        _modelsFound = true;
        hands.IsEnabled = true;
        Assert.True(hands.HasModels);
    }

    [Fact]
    public void Results_show_only_while_active_and_clear_when_it_stops()
    {
        var hands = CreateViewModel(new HandSettings { Enabled = true });
        hands.ShowResult(Result(2));
        Assert.Empty(hands.Hands);

        hands.IsActive = true;
        hands.ShowStatus(new VisionStatus(VisionState.Running, null, "DirectML", null));
        Assert.True(hands.HasStats);
        hands.ShowResult(Result(2));
        Assert.Equal(2, hands.Hands.Count);
        Assert.Contains("2", hands.StatsText);
        Assert.Contains("30 fps", hands.StatsText);
        Assert.Contains("DirectML", hands.StatsText);

        hands.ShowStatus(new VisionStatus(VisionState.Failed, null, null, "no GPU"));
        Assert.Empty(hands.Hands);
        Assert.Contains("no GPU", hands.ErrorText);

        hands.IsActive = false;
        Assert.False(hands.HasError);
        Assert.False(hands.HasStats);
    }

    [Fact]
    public void Exit_saves_a_pending_change()
    {
        var hands = CreateViewModel();
        hands.IsEnabled = true;

        hands.SaveNow();
        hands.SaveNow();

        Assert.Single(_saved);
    }

    // Shots

    [Fact]
    public void Shots_play_in_the_preview_only_while_switched_on()
    {
        var hands = CreateViewModel(new HandSettings { Enabled = true });
        hands.IsActive = true;

        hands.ShowResult(Result(1, shots: 1));
        Assert.Single(hands.Shots);
        hands.ShowResult(Result(1, shots: 2));
        Assert.Equal(3, hands.Shots.Count);   // earlier ones still playing

        hands.ShotsEnabled = false;
        Assert.Equal(new HandSettings { Enabled = true, Shots = false }, _applied[^1]);
        hands.ShowResult(Result(1, shots: 1));
        Assert.Equal(3, hands.Shots.Count);

        hands.IsActive = false;
        Assert.Empty(hands.Shots);
    }

    [Fact]
    public void Shot_struct_matches_the_dll()
    {
        Assert.Equal(20, Marshal.SizeOf<HitCamShot>());
        var shot = HitCamShot.From(new Shot(3, new PointF(0.25f, 0.75f), new PointF(0.6f, -0.8f), 0.3f));
        Assert.Equal((0.25f, 0.75f, 0.6f, -0.8f, 0.3f), (shot.X, shot.Y, shot.DirX, shot.DirY, shot.Size));
    }

    [Fact]
    public void Shot_curves_match_the_dll()
    {
        // The values HitCamVCamTest --shot checks for ShotEffect::At.
        var start = ShotEffect.At(0, 1);
        Assert.Equal(1, start.Flash);
        Assert.Equal(0.22f, start.Lift, 1e-4f);
        Assert.Equal(0.018f, start.ShakeY, 1e-5f);
        Assert.Equal(-0.009f, start.ShakeX, 1e-5f);
        Assert.Equal(1.0396f, start.Zoom, 1e-4f);
        Assert.Equal(0.009f, ShotEffect.At(0, -1).ShakeX, 1e-5f);

        var middle = ShotEffect.At(100, 1);
        Assert.True(middle.Flash < 0.1f && middle.Lift < 0.03f && MathF.Abs(middle.ShakeY) < 0.01f);
        Assert.Equal(ShotState.None, ShotEffect.At(ShotEffect.DurationMs, 1));
        Assert.Equal(ShotState.None, ShotEffect.At(-1, 1));

        // The zoom always covers the shake: no frame edge shows.
        for (var ms = 0.0; ms < ShotEffect.DurationMs; ms += 5)
        {
            var s = ShotEffect.At(ms, 1);
            Assert.True((s.Zoom - 1) / 2 >= MathF.Abs(s.ShakeY) - 1e-6f, $"{ms} ms");
        }
    }

    // Overlay

    [Fact]
    public void The_skeleton_joins_all_21_points_in_one_piece()
    {
        var connections = HandOverlay.Connections;
        Assert.Equal(21, connections.Count);
        Assert.Equal(Enumerable.Range(0, 21), connections.SelectMany(c => new[] { c.From, c.To }).Distinct().Order());

        // Every point is reachable from the wrist.
        var reached = new HashSet<int> { 0 };
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (from, to) in connections)
                changed |= reached.Contains(from) ? reached.Add(to) : reached.Contains(to) && reached.Add(from);
        }
        Assert.Equal(21, reached.Count);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(12, 3)]
    [InlineData(13, 4)]
    [InlineData(20, 5)]
    public void Points_are_coloured_by_finger(int landmark, int finger)
    {
        Assert.Equal(finger, HandOverlay.FingerOf(landmark));
    }

    [Fact]
    public void Fingertips_are_the_last_point_of_each_finger()
    {
        Assert.Equal([4, 8, 12, 16, 20], Enumerable.Range(0, 21).Where(HandOverlay.IsFingertip));
    }
}

using System.Reactive.Concurrency;
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

    private static HandResult Result(int hands) => new(
        [.. Enumerable.Range(1, hands).Select(id => new TrackedHand(id, new PointF[21], 0.9f, true))],
        new VisionStats(4.4, 29.6, "DirectML"), 1, 960, 540);

    // Settings

    [Fact]
    public void Settings_default_to_off_without_lines()
    {
        var settings = AppSettings.Parse("""{"serverId":"a"}"""u8)!;

        Assert.False(settings.Hands.Enabled);
        Assert.False(settings.Hands.ShowSkeleton);
        Assert.Equal(new HandSettings(), AppSettings.Parse("""{"hands":null}"""u8)!.Hands);
        Assert.True(AppSettings.Parse("""{"hands":{"enabled":true}}"""u8)!.Hands.Enabled);
    }

    [Fact]
    public void Hand_settings_survive_a_save_and_load()
    {
        var path = Path.Combine(_directory, "settings.json");
        var hands = new HandSettings { Enabled = true, ShowSkeleton = true };

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

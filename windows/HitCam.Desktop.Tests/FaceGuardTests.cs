using HitCam.Desktop.Services;

namespace HitCam.Desktop.Tests;

public sealed class FaceGuardTests
{
    private sealed class ManualTime : TimeProvider
    {
        public long Ticks { get; private set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Ticks;

        public void Advance(TimeSpan by) => Ticks += by.Ticks;
    }

    private static readonly FaceSettings Settings = new() { Effect = FaceEffectKind.Blur, Strength = 50 };

    private static HitCamFaceRegion Region(float left) => new() { Left = left, Top = 0.1f, Right = left + 0.2f, Bottom = 0.4f };

    [Fact]
    public void Before_the_first_result_the_whole_picture_is_covered()
    {
        var guard = new FaceGuard(new ManualTime());

        Assert.True(guard.CoversAll);
        var cover = Assert.Single(guard.Due(Settings)!);
        Assert.Equal((0f, 0f, 1f, 1f), (cover.Left, cover.Top, cover.Right, cover.Bottom));
        Assert.Equal((int)FaceEffectKind.Blur, cover.Effect);
        Assert.Equal(0.5f, cover.Strength);
    }

    [Fact]
    public void Late_results_are_bridged_by_sending_the_last_regions_again()
    {
        var time = new ManualTime();
        var guard = new FaceGuard(time);
        HitCamFaceRegion[] regions = [Region(0.3f)];
        guard.Sent(regions);
        Assert.False(guard.CoversAll);

        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Null(guard.Due(Settings));               // fresh enough, the DLL still has them

        time.Advance(FaceGuard.Refresh);
        Assert.Same(regions, guard.Due(Settings));      // analysis is late: again, before the DLL drops them
        Assert.Null(guard.Due(Settings));
        time.Advance(FaceGuard.Refresh);
        Assert.Same(regions, guard.Due(Settings));
    }

    [Fact]
    public void No_faces_means_nothing_to_send_and_a_reset_covers_everything_again()
    {
        var time = new ManualTime();
        var guard = new FaceGuard(time);
        guard.Sent([]);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(guard.Due(Settings));

        guard.Reset();   // e.g. a new stream: the old places mean nothing
        Assert.True(guard.CoversAll);
        Assert.Single(guard.Due(Settings)!);
    }
}

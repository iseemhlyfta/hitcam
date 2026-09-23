using HitCam.Core.Pairing;
using HitCam.Core.Server;
using HitCam.Core.Video;

namespace HitCam.Core.Tests;

public class AnnexBTests
{
    private static readonly byte[] Keyframe =
    [
        0, 0, 0, 1, 0x67, 0xAA, 0xBB,   // SPS, 4-byte start code
        0, 0, 1, 0x68, 0xCC,            // PPS, 3-byte start code
        0, 0, 0, 1, 0x65, 0x11, 0x22,   // IDR slice
    ];

    [Fact]
    public void Splits_units_with_both_start_code_lengths()
    {
        var units = AnnexB.SplitNalUnits(Keyframe).Select(r => Keyframe[r]).ToArray();

        Assert.Equal(3, units.Length);
        Assert.Equal([0x67, 0xAA, 0xBB], units[0]);
        Assert.Equal([0x68, 0xCC], units[1]);
        Assert.Equal([0x65, 0x11, 0x22], units[2]);
    }

    [Fact]
    public void Detects_decodable_keyframes()
    {
        Assert.True(AnnexB.IsDecodableKeyframe(Keyframe));
        Assert.False(AnnexB.IsDecodableKeyframe(Keyframe.AsSpan(12)));
        Assert.False(AnnexB.IsDecodableKeyframe([0, 0, 1, 0x41, 0x9A]));
        Assert.Empty(AnnexB.SplitNalUnits([1, 2, 3]));
    }
}

public class PairingTests
{
    [Fact]
    public void Correct_pin_is_accepted_once()
    {
        var guard = new PinGuard();
        var pin = guard.Begin()!;

        Assert.Matches("^[0-9]{6}$", pin);
        Assert.Equal(PinCheckResult.Ok, guard.Check(pin));
        Assert.Equal(PinCheckResult.Exhausted, guard.Check(pin));
    }

    [Fact]
    public void Five_wrong_pins_lock_pairing_for_30_seconds()
    {
        var time = new ManualTime();
        var guard = new PinGuard(time);
        var pin = guard.Begin()!;
        var wrong = pin == "000000" ? "000001" : "000000";

        for (var i = 0; i < PinGuard.MaxAttempts - 1; i++)
            Assert.Equal(PinCheckResult.Wrong, guard.Check(wrong));
        Assert.Equal(PinCheckResult.Exhausted, guard.Check(wrong));

        Assert.True(guard.IsLockedOut);
        Assert.Null(guard.Begin());
        time.Advance(PinGuard.Lockout);
        Assert.NotNull(guard.Begin());
    }

    [Fact]
    public void Store_verifies_only_the_latest_token_of_a_device()
    {
        var store = new InMemoryPairingStore();
        var first = store.Pair("phone-1", "iPhone");
        var second = store.Pair("phone-1", "iPhone");

        Assert.False(store.Verify("phone-1", first));
        Assert.True(store.Verify("phone-1", second));
        Assert.False(store.Verify("phone-2", second));
        Assert.False(store.Verify("phone-1", null));
        Assert.Single(store.Devices);
        Assert.DoesNotContain(store.Devices, d => d.TokenHash == second);
    }

    [Fact]
    public void File_store_survives_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hitcam-test-{Guid.NewGuid()}", "devices.json");
        try
        {
            var token = new FilePairingStore(path).Pair("phone-1", "iPhone");

            var reloaded = new FilePairingStore(path);
            Assert.True(reloaded.Verify("phone-1", token));
            Assert.True(reloaded.Remove("phone-1"));
            Assert.False(new FilePairingStore(path).Verify("phone-1", token));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}

public class ClockSyncTests
{
    [Fact]
    public void Offset_comes_from_the_fastest_round_trip()
    {
        var clock = new ClockSync();
        Assert.Null(clock.ToLocal(1));

        // Phone clock runs 1_000_000 us ahead. First sample has an asymmetric slow return path.
        clock.AddSample(pcSent: 100, phoneReplied: 1_000_110, pcReceived: 900);
        clock.AddSample(pcSent: 2000, phoneReplied: 1_002_010, pcReceived: 2020);

        Assert.Equal(1_000_000, clock.OffsetMicros);
        Assert.Equal(5000UL, clock.ToLocal(1_005_000));
    }
}

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

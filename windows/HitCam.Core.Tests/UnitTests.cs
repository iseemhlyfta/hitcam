using HitCam.Core.Pairing;
using HitCam.Core.Server;

namespace HitCam.Core.Tests;

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

        Assert.Null(guard.Begin());
        time.Advance(PinGuard.Lockout - TimeSpan.FromSeconds(1));
        Assert.Null(guard.Begin());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(guard.Begin());
        Assert.Equal(PinGuard.MaxAttempts, guard.AttemptsLeft);
    }

    [Fact]
    public void Wrong_pins_add_up_across_pairing_windows()
    {
        var time = new ManualTime();
        var guard = new PinGuard(time);

        // A new connection (Begin) or a dropped one (Cancel) does not give the attempts back.
        for (var i = 0; i < PinGuard.MaxAttempts - 1; i++)
        {
            Assert.Equal(PinCheckResult.Wrong, guard.Check(WrongPin(guard.Begin()!)));
            guard.Cancel();
            time.Advance(TimeSpan.FromSeconds(10));
        }

        var pin = guard.Begin()!;
        Assert.Equal(1, guard.AttemptsLeft);
        Assert.Equal(PinCheckResult.Exhausted, guard.Check(WrongPin(pin)));
        Assert.Null(guard.Begin());
    }

    [Fact]
    public void Each_lockout_doubles_up_to_ten_minutes()
    {
        var time = new ManualTime();
        var guard = new PinGuard(time);
        TimeSpan[] expected =
        [
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(240),
            TimeSpan.FromSeconds(480), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10),
        ];

        foreach (var lockout in expected)
        {
            ExhaustAttempts(guard);
            time.Advance(lockout - TimeSpan.FromSeconds(1));
            Assert.Null(guard.Begin());
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.NotNull(guard.Begin());
            guard.Cancel();
        }
    }

    [Fact]
    public void Correct_pin_or_a_quiet_window_resets_the_count()
    {
        var time = new ManualTime();
        var guard = new PinGuard(time);

        ExhaustAttempts(guard);
        time.Advance(PinGuard.Lockout);
        var pin = guard.Begin()!;
        Assert.Equal(PinCheckResult.Wrong, guard.Check(WrongPin(pin)));
        Assert.Equal(PinCheckResult.Ok, guard.Check(pin));

        // Back to a 30 s lockout after success.
        ExhaustAttempts(guard);
        time.Advance(PinGuard.Lockout);
        Assert.NotNull(guard.Begin());

        Assert.Equal(PinCheckResult.Wrong, guard.Check(WrongPin(guard.Begin()!)));
        time.Advance(PinGuard.FailureWindow);
        guard.Begin();
        Assert.Equal(PinGuard.MaxAttempts, guard.AttemptsLeft);
    }

    private static void ExhaustAttempts(PinGuard guard)
    {
        var pin = guard.Begin()!;
        for (var i = 0; i < PinGuard.MaxAttempts - 1; i++)
            Assert.Equal(PinCheckResult.Wrong, guard.Check(WrongPin(pin)));
        Assert.Equal(PinCheckResult.Exhausted, guard.Check(WrongPin(pin)));
    }

    private static string WrongPin(string pin) => pin == "000000" ? "000001" : "000000";

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

    [Fact]
    public void File_store_is_unchanged_when_saving_fails()
    {
        var path = TempStorePath();
        try
        {
            var time = new ManualTime();
            var store = new FilePairingStore(path, time);
            var token = store.Pair("phone-1", "iPhone");
            // A directory where the temp file goes makes every save fail.
            Directory.CreateDirectory(path + ".tmp");

            Assert.ThrowsAny<Exception>(() => store.Pair("phone-2", "iPhone 2"));
            Assert.Equal(["phone-1"], store.Devices.Select(d => d.DeviceId));

            time.Advance(TimeSpan.FromHours(1));
            store.Touch("phone-1");
            Assert.True(store.Verify("phone-1", token));
            Assert.True(new FilePairingStore(path).Verify("phone-1", token));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_file_store_starts_empty_and_keeps_a_backup()
    {
        var path = TempStorePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """[{"deviceId":null}]""");

            var store = new FilePairingStore(path);
            Assert.Empty(store.Devices);
            Assert.Equal("""[{"deviceId":null}]""", File.ReadAllText(path + ".bak"));

            store.Pair("phone-1", "iPhone");
            Assert.Single(new FilePairingStore(path).Devices);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Locked_file_store_does_not_throw()
    {
        var path = TempStorePath();
        try
        {
            var token = new FilePairingStore(path).Pair("phone-1", "iPhone");
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Empty(new FilePairingStore(path).Devices);

            Assert.True(new FilePairingStore(path).Verify("phone-1", token));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private static string TempStorePath() =>
        Path.Combine(Path.GetTempPath(), $"hitcam-test-{Guid.NewGuid()}", "devices.json");
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

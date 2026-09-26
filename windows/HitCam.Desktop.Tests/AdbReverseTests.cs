using HitCam.Desktop.Services;

namespace HitCam.Desktop.Tests;

public sealed class AdbReverseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HitCam.Tests." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Devices_are_read_by_state()
    {
        const string output = "* daemon started successfully\r\nList of devices attached\r\nR58M123\tdevice\r\nemulator-5554\tdevice\r\nABC\tunauthorized\r\nXYZ\toffline\r\n\r\n";
        var (ready, unauthorized) = AdbReverse.ParseDevices(output);
        Assert.Equal(["R58M123", "emulator-5554"], ready);
        Assert.Equal(["ABC"], unauthorized);
    }

    [Fact]
    public void Nothing_attached_is_no_phone()
    {
        var (ready, unauthorized) = AdbReverse.ParseDevices("List of devices attached\n\n");
        Assert.Empty(ready);
        Assert.Empty(unauthorized);
    }

    [Fact]
    public async Task A_plugged_in_phone_gets_the_pc_port_reversed_once()
    {
        // A stand-in adb: lists one phone and logs every call.
        Directory.CreateDirectory(_directory);
        var log = Path.Combine(_directory, "calls.txt");
        var adb = Path.Combine(_directory, "adb.cmd");
        File.WriteAllText(adb, $"""
            @echo off
            echo %*>>"{log}"
            if "%1"=="devices" (
              echo List of devices attached
              echo PHONE1	device
            )
            exit /b 0
            """);
        var states = new List<UsbState>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var usb = new AdbReverse(47899, adb))
        {
            usb.StateChanged += state =>
            {
                lock (states)
                    states.Add(state);
                if (state == UsbState.Ready)
                    ready.TrySetResult();
            };
            usb.Start();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            // Another round: the rule is set again (a restarted adb server forgets it while the phone stays listed).
            await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        var calls = File.ReadAllLines(log).Select(c => c.Trim()).ToList();
        Assert.True(calls.Count(c => c == "devices") >= 2);
        Assert.True(calls.Count(c => c == "-s PHONE1 reverse tcp:47800 tcp:47899") >= 2);
        Assert.All(calls, c => Assert.True(c == "devices" || c == "-s PHONE1 reverse tcp:47800 tcp:47899", c));
    }

    [Fact]
    public async Task Without_adb_it_says_how_to_get_it()
    {
        var state = new TaskCompletionSource<UsbState>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var usb = new AdbReverse(47800, Path.Combine(_directory, "missing", "adb.exe"));
        usb.StateChanged += s => state.TrySetResult(s);
        usb.Start();
        Assert.Equal(UsbState.NoAdb, await state.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}

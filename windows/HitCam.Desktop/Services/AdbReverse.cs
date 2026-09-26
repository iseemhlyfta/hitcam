using System.Diagnostics;

namespace HitCam.Desktop.Services;

/// <summary>What <see cref="AdbReverse"/> sees.</summary>
public enum UsbState
{
    /// <summary>No adb on this PC (HitCam does not ship it: the Android SDK license).</summary>
    NoAdb,
    /// <summary>adb found, no Android phone with USB debugging plugged in.</summary>
    NoPhone,
    /// <summary>A phone is plugged in but has not allowed this PC yet (the "Allow USB debugging?" prompt).</summary>
    Unauthorized,
    /// <summary>At least one phone reaches this PC over USB at 127.0.0.1.</summary>
    Ready,
}

/// <summary>
/// Android over USB: with adb installed (Android SDK platform-tools), every phone plugged in with USB debugging gets
/// <c>adb reverse tcp:47800 tcp:&lt;port&gt;</c>, so its HitCam app reaches this PC at 127.0.0.1 with no Wi-Fi. The
/// connection is the same TLS one as over Wi-Fi. Checks every few seconds on a pool thread.
/// </summary>
public sealed class AdbReverse : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    private readonly string? _adb;
    private readonly int _pcPort;
    private readonly Timer _timer;
    private readonly HashSet<string> _reversed = [];
    private int _busy;
    private UsbState? _state;

    public AdbReverse(int pcPort, string? adb = null)
    {
        _pcPort = pcPort;
        _adb = adb ?? Find();
        if (_adb is not null && !File.Exists(_adb))
            _adb = null;
        _timer = new Timer(_ => Check(), null, Timeout.InfiniteTimeSpan, Interval);
    }

    /// <summary>Starts checking (subscribe to <see cref="StateChanged"/> first).</summary>
    public void Start() => _timer.Change(TimeSpan.Zero, Interval);

    /// <summary>Raised on a pool thread when the state changes.</summary>
    public event Action<UsbState>? StateChanged;

    /// <summary>adb.exe from ANDROID_HOME / ANDROID_SDK_ROOT, the default SDK folder, or PATH; null if none.</summary>
    public static string? Find()
    {
        var candidates = new List<string>();
        foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } sdk)
                candidates.Add(Path.Combine(sdk, "platform-tools", "adb.exe"));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"));
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(folder.Trim('"'), "adb.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Serials of <c>adb devices</c> lines by state: "device" (usable) and "unauthorized".</summary>
    public static (List<string> Ready, List<string> Unauthorized) ParseDevices(string output)
    {
        List<string> ready = [], unauthorized = [];
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*'))
                continue;
            if (parts[1] == "device")
                ready.Add(parts[0]);
            else if (parts[1] == "unauthorized")
                unauthorized.Add(parts[0]);
        }
        return (ready, unauthorized);
    }

    private void Check()
    {
        // A slow adb (starting its server) must not pile up checks.
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            if (_adb is null)
            {
                Report(UsbState.NoAdb);
                return;
            }
            if (Run("devices") is not { } output)
                return;
            var (ready, unauthorized) = ParseDevices(output);
            // Unplugged phones lose their reverse; plugged in again they need a new one.
            _reversed.IntersectWith(ready);
            foreach (var serial in ready.Where(s => !_reversed.Contains(s)))
            {
                if (Run("-s", serial, "reverse", $"tcp:{Core.Protocol.ProtocolInfo.DefaultPort}", $"tcp:{_pcPort}") is not null)
                    _reversed.Add(serial);
            }
            Report(_reversed.Count > 0 ? UsbState.Ready : unauthorized.Count > 0 ? UsbState.Unauthorized : UsbState.NoPhone);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"HitCam: adb check failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void Report(UsbState state)
    {
        if (_state == state)
            return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>The command's output if it ran and succeeded in time, else null.</summary>
    private string? Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(_adb!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null)
            return null;
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CommandTimeout))
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            return null;
        }
        return process.ExitCode == 0 ? output.Result : null;
    }

    public void Dispose() => _timer.Dispose();
}

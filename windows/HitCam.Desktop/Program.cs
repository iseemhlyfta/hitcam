using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using HitCam.Desktop.Services;
using ReactiveUI.Avalonia;

namespace HitCam.Desktop;

internal static partial class Program
{
    /// <summary>Port from <c>--port N</c> (development: a second instance that phones do not find).</summary>
    public static int? PortOverride { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var index = Array.IndexOf(args, "--port");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var port))
            PortOverride = port;

        // One HitCam per user: a second launch (e.g. from the Start menu) brings the running window forward.
        using var mutex = new Mutex(true, @"Local\HitCam.Desktop", out var isFirst);
        if (!isFirst && PortOverride is null)
        {
            if (ActivateRunningInstance())
                return 0;
            // No window: the other instance may be closing down (or still starting). Give it a moment.
            if (!WaitForMutex(mutex, TimeSpan.FromSeconds(2)))
            {
                if (!ActivateRunningInstance())
                    MessageBox(IntPtr.Zero, Loc.AlreadyRunningNotResponding, "HitCam", MbIconWarning);
                return 0;
            }
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI(_ => { });

    private static bool WaitForMutex(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // The other instance exited; the mutex is ours now.
            return true;
        }
    }

    private static bool ActivateRunningInstance()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id == current.Id || other.MainWindowHandle == IntPtr.Zero)
                    continue;
                ShowWindow(other.MainWindowHandle, SwRestore);
                SetForegroundWindow(other.MainWindowHandle);
                return true;
            }
        }
        return false;
    }

    private const int SwRestore = 9;
    private const uint MbIconWarning = 0x30;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr owner, string text, string caption, uint type);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
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
            ActivateRunningInstance();
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI(_ => { });

    private static void ActivateRunningInstance()
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
                return;
            }
        }
    }

    private const int SwRestore = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);
}

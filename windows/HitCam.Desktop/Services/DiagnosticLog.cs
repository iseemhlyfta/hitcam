using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace HitCam.Desktop.Services;

/// <summary>
/// A small text log for finding out why the app stopped: start and exit, why the window closed, unhandled
/// exceptions from any thread, warnings (Trace) and notable events such as shots. Every line is written through at
/// once, so the last lines survive a crash. <c>%LOCALAPPDATA%\HitCam\logs\hitcam.log</c>, the previous one kept as
/// <c>hitcam.1.log</c> once it passes 1 MB. Never throws.
/// </summary>
public static class DiagnosticLog
{
    public const long MaxBytes = 1024 * 1024;

    private static readonly Lock Gate = new();
    private static bool _installed;

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HitCam", "logs", "hitcam.log");

    /// <summary>Where lines go; tests point it elsewhere.</summary>
    public static string FilePath { get; set; } = DefaultPath;

    /// <summary>Hooks unhandled exceptions, process exit and Trace warnings. Call once, first thing in Main.</summary>
    public static void Install()
    {
        lock (Gate)
        {
            if (_installed)
                return;
            _installed = true;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write($"FATAL unhandled exception (terminating: {e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Write($"unobserved task exception: {e.Exception}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write($"process exit, code {Environment.ExitCode}");
        Trace.Listeners.Add(new Listener { Filter = new EventTypeFilter(SourceLevels.Warning) });
    }

    public static void Write(string message)
    {
        try
        {
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(FilePath, Path.ChangeExtension(FilePath, ".1.log"), overwrite: true);
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Logging must never take the app down.
        }
    }

    /// <summary>Trace warnings and errors (the app's and Avalonia's) into the log.</summary>
    private sealed class Listener : TraceListener
    {
        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                DiagnosticLog.Write(message);
        }
    }
}

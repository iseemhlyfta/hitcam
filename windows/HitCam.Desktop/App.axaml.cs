using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HitCam.Desktop.Services;
using HitCam.Desktop.ViewModels;
using HitCam.Desktop.Views;

namespace HitCam.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Logged, not handled: the crash still happens, but the log says why.
        Dispatcher.UIThread.UnhandledException += (_, e) => DiagnosticLog.Write($"FATAL on the UI thread: {e.Exception}");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            // Exit is raised on every shutdown path (last window closed, OS logoff, forced Shutdown), unlike
            // ShutdownRequested. It runs on the UI thread, so the view model must not need that thread to finish.
            desktop.Exit += (_, e) =>
            {
                DiagnosticLog.Write($"exit requested, code {e.ApplicationExitCode}");
                viewModel.Shutdown(TimeSpan.FromSeconds(3));
            };
            viewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

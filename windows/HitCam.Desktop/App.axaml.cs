using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HitCam.Desktop.ViewModels;
using HitCam.Desktop.Views;

namespace HitCam.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) => viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            viewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

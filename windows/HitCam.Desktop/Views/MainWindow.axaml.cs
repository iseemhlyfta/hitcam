using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using HitCam.Desktop.Services;
using HitCam.Desktop.ViewModels;

namespace HitCam.Desktop.Views;

public partial class MainWindow : Window
{
    private WindowState _stateBeforeFullScreen = WindowState.Normal;

    public MainWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel viewModel)
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // The view model only says "full screen"; the window owns how that looks.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsFullScreen) || sender is not MainViewModel viewModel)
            return;

        if (viewModel.IsFullScreen && WindowState != WindowState.FullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else if (!viewModel.IsFullScreen && WindowState == WindowState.FullScreen)
        {
            WindowState = _stateBeforeFullScreen;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        DiagnosticLog.Write($"window closing: {e.CloseReason}{(e.IsProgrammatic ? ", programmatic" : "")}");
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel { IsFullScreen: true } viewModel)
        {
            viewModel.IsFullScreen = false;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using HitCam.Desktop.ViewModels;

namespace HitCam.Desktop.Views;

public partial class WaitingView : UserControl
{
    public WaitingView() => InitializeComponent();

    private async void OnCopyAddress(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(viewModel.PrimaryAddress);
    }
}

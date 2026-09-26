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
        if (DataContext is not MainViewModel viewModel || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(viewModel.PrimaryAddress);
        }
        catch (Exception ex)
        {
            // The clipboard held by another program (a clipboard manager, remote desktop): not worth a crash.
            System.Diagnostics.Trace.TraceWarning($"Copy address: {ex.Message}");
        }
    }
}

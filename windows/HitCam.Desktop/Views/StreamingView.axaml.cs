using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using HitCam.Desktop.ViewModels;

namespace HitCam.Desktop.Views;

public partial class StreamingView : UserControl
{
    public StreamingView() => InitializeComponent();

    private async void OnChooseBackgroundImage(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = HitCam.Desktop.Services.Loc.Background,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new Avalonia.Platform.Storage.FilePickerFileType(HitCam.Desktop.Services.Loc.BackgroundImageFilter)
                    {
                        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.gif"],
                    },
                ],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
                viewModel.Background.SetImage(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Background picture: {ex.Message}");
        }
    }

    private void OnPreviewDoubleTapped(object? sender, TappedEventArgs e)
    {
        // A double click is for full screen only: its first click must not uncover or hide a face.
        FaceSquares.CancelPendingToggle();
        if (DataContext is MainViewModel viewModel)
            viewModel.IsFullScreen = !viewModel.IsFullScreen;
    }
}

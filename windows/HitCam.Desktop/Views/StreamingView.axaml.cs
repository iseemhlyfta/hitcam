using Avalonia.Controls;
using Avalonia.Input;
using HitCam.Desktop.ViewModels;

namespace HitCam.Desktop.Views;

public partial class StreamingView : UserControl
{
    public StreamingView() => InitializeComponent();

    private void OnPreviewDoubleTapped(object? sender, TappedEventArgs e)
    {
        // A double click is for full screen only: its first click must not uncover or hide a face.
        FaceSquares.CancelPendingToggle();
        if (DataContext is MainViewModel viewModel)
            viewModel.IsFullScreen = !viewModel.IsFullScreen;
    }
}

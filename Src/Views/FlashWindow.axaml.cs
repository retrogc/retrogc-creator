using Avalonia.Controls;
using RetroGcCreator.ViewModels;

namespace RetroGcCreator.Views;

public partial class FlashWindow : Window
{
    private readonly FlashViewModel _viewModel;

    // XAML loader / designer.
    public FlashWindow() : this(null!)
    {
    }

    public FlashWindow(FlashViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        PixelPerfect.Track(this, 1);

        if (viewModel is null)
            return;

        viewModel.CloseRequested += (_, _) => Close();
        Opened += async (_, _) => await viewModel.RefreshAsync();

        // Never close in the middle of a write.
        Closing += (_, e) =>
        {
            if (_viewModel.IsWriting)
                e.Cancel = true;
        };
    }
}

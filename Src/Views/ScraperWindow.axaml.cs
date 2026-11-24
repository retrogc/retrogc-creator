using Avalonia.Controls;
using RetroGcCreator.ViewModels;

namespace RetroGcCreator.Views;

public partial class ScraperWindow : Window
{
    // XAML loader / designer.
    public ScraperWindow() : this(null!)
    {
    }

    public ScraperWindow(ScraperViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        PixelPerfect.Track(this, 1);

        if (viewModel is null)
            return;

        viewModel.CloseRequested += (_, selection) => Close(selection);
        Opened += async (_, _) => await viewModel.InitializeAsync();
        Closed += (_, _) => viewModel.Dispose();
    }
}

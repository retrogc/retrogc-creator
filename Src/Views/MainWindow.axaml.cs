using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using RetroGcCreator.ViewModels;

namespace RetroGcCreator.Views;

public partial class MainWindow : Window, IDialogs
{
    private readonly MainViewModel _viewModel;
    private bool _closeConfirmed;

    public MainWindow()
    {
        _viewModel = new MainViewModel(this);
        DataContext = _viewModel;
        InitializeComponent();
        PixelPerfect.Track(this, 1);

        Closing += OnClosing;
        Closed += (_, _) => _viewModel.Dispose();
    }

    /// <summary>Opens a card given on the command line (folder, .rgc, or a card image to view).</summary>
    public void OpenFromArgument(string path)
    {
        if (System.IO.Directory.Exists(path) && Services.CardFolder.FindDefinition(path) is { } definition)
            _viewModel.Open(definition);
        else if (System.IO.File.Exists(path) && MainViewModel.IsImageFile(path))
        {
            // Unpacking shows progress and maybe error dialogs: wait for the window to be shown.
            if (IsVisible)
                _ = _viewModel.ViewImageAsync(path);
            else
            {
                void OnOpened(object? sender, EventArgs e)
                {
                    Opened -= OnOpened;
                    _ = _viewModel.ViewImageAsync(path);
                }

                Opened += OnOpened;
            }
        }
        else if (System.IO.File.Exists(path))
            _viewModel.Open(path);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !_viewModel.IsDirty)
            return;

        e.Cancel = true;
        if (await _viewModel.ConfirmDiscardAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    // ------------------------------------------------------------ IDialogs

    public async Task<string?> PickFolderAsync(string title, string? startFolder = null)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartAsync(startFolder ?? _viewModel.CardRoot)
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, bool multiple, string? startFolder = null,
        string? filterName = null, IReadOnlyList<string>? patterns = null)
    {
        var types = new List<FilePickerFileType>();
        if (filterName is not null && patterns is not null)
            types.Add(new FilePickerFileType(filterName) { Patterns = patterns });
        types.Add(FilePickerFileTypes.All);

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = types,
            SuggestedStartLocation = await StartAsync(startFolder ?? _viewModel.CardRoot)
        });

        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string? startFolder = null)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "xz",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Card image") { Patterns = ["*.img.xz"] }],
            SuggestedStartLocation = await StartAsync(startFolder)
        });

        return file?.TryGetLocalPath();
    }

    public Task<bool> ConfirmAsync(string title, string message, string yes = "Yes", string no = "Cancel") =>
        MessageWindow.ShowAsync(this, title, message, yes, no);

    public async Task<bool> ConfirmListAsync(string title, string message, System.Collections.Generic.IReadOnlyList<string> items,
        string yes, string no)
    {
        // A card given on the command line is opened before the window shows: a dialog needs it shown.
        if (!IsVisible)
        {
            var opened = new TaskCompletionSource();
            Opened += (_, _) => opened.TrySetResult();
            if (!IsVisible)
                await opened.Task;
        }

        return await MessageWindow.ShowListAsync(this, title, message, items, yes, no);
    }

    public Task ShowErrorAsync(string title, string message) =>
        MessageWindow.ShowAsync(this, title, message, "OK", null);

    public Task<int> ChooseAsync(string title, string message, string[] options) =>
        MessageWindow.ChooseAsync(this, title, message, options);

    public async Task ShowFlashAsync(string image)
    {
        var window = new FlashWindow(new FlashViewModel(image));
        await window.ShowDialog(this);
    }

    public Task<ScrapeSelection?> ShowScraperAsync(ScraperViewModel viewModel) =>
        new ScraperWindow(viewModel).ShowDialog<ScrapeSelection?>(this);

    private async Task<IStorageFolder?> StartAsync(string? folder) =>
        string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder)
            ? null
            : await StorageProvider.TryGetFolderFromPathAsync(folder);
}

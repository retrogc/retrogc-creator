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

        Closing += OnClosing;
        Closed += (_, _) => _viewModel.Dispose();
    }

    /// <summary>Opens a card given on the command line (folder or .rgc).</summary>
    public void OpenFromArgument(string path)
    {
        if (System.IO.Directory.Exists(path) && Services.CardFolder.FindDefinition(path) is { } definition)
            _viewModel.Open(definition);
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

    public Task ShowErrorAsync(string title, string message) =>
        MessageWindow.ShowAsync(this, title, message, "OK", null);

    public async Task ShowFlashAsync(string image)
    {
        var window = new FlashWindow(new FlashViewModel(image));
        await window.ShowDialog(this);
    }

    private async Task<IStorageFolder?> StartAsync(string? folder) =>
        string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder)
            ? null
            : await StorageProvider.TryGetFolderFromPathAsync(folder);
}

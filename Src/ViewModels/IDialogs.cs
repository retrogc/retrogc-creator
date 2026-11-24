using System.Collections.Generic;
using System.Threading.Tasks;

namespace RetroGcCreator.ViewModels;

/// <summary>File pickers and prompts, implemented by the window.</summary>
public interface IDialogs
{
    Task<string?> PickFolderAsync(string title, string? startFolder = null);

    Task<IReadOnlyList<string>> PickFilesAsync(string title, bool multiple, string? startFolder = null,
        string? filterName = null, IReadOnlyList<string>? patterns = null);

    Task<string?> PickSaveFileAsync(string title, string suggestedName, string? startFolder = null);

    /// <summary>Yes / No question.</summary>
    Task<bool> ConfirmAsync(string title, string message, string yes = "Yes", string no = "Cancel");

    /// <summary>Yes / No question with a list of items (files...) shown under the message.</summary>
    Task<bool> ConfirmListAsync(string title, string message, IReadOnlyList<string> items, string yes, string no);

    Task ShowErrorAsync(string title, string message);

    /// <summary>Several choices; returns the index of the chosen one, or -1 when the window is closed.</summary>
    Task<int> ChooseAsync(string title, string message, string[] options);

    /// <summary>Opens the "write to USB" window for an image.</summary>
    Task ShowFlashAsync(string image);

    /// <summary>Opens the ScreenScraper window; returns what to put in the card, or null when cancelled.</summary>
    Task<ScrapeSelection?> ShowScraperAsync(ScraperViewModel viewModel);
}

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

    Task ShowErrorAsync(string title, string message);

    /// <summary>Opens the "write to USB" window for an image.</summary>
    Task ShowFlashAsync(string image);
}

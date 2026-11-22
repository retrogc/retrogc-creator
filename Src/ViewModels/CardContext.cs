using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RetroGcCreator.Services;

namespace RetroGcCreator.ViewModels;

/// <summary>What path editors need from the open card: its folder, dialogs and change notifications.</summary>
public sealed class CardContext(IDialogs dialogs, Action changed, Action filesChanged)
{
    public IDialogs Dialogs { get; } = dialogs;

    /// <summary>Card folder (root of the future volume).</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Set by the editor: gives the default folders their context.</summary>
    public CardEditorViewModel? Editor { get; set; }

    /// <summary>Suspends change tracking while a card is loaded.</summary>
    public bool Loading { get; set; }

    public void MarkChanged()
    {
        if (!Loading)
            changed();
    }

    public void FilesChanged() => filesChanged();

    /// <summary>Set by the main view: looks for files the card doesn't use and offers to remove them.</summary>
    public Func<Task>? UnusedFilesCheck { get; set; }

    public Task CheckUnusedFilesAsync() => UnusedFilesCheck?.Invoke() ?? Task.CompletedTask;

    /// <summary>
    /// A line stopped using <paramref name="relative"/> (removed, or replaced by another file): when the file
    /// is in the card and nothing else in the card uses it (e.g. the same ROM for RetroArch and MiSTer),
    /// offers to move it to the Recycle Bin.
    /// </summary>
    public async Task OfferDeleteIfUnusedAsync(string relative)
    {
        if (Editor is not { } editor || Root.Length == 0)
            return;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = CardFolder.Resolve(Root, relative.Trim().TrimStart('/'));
        if (full is null || !File.Exists(full) || editor.AllPaths().Any(p =>
                CardFolder.Resolve(Root, p.Entry.Path.Trim().TrimStart('/')) is { } other &&
                string.Equals(other, full, comparison)))
            return;

        var trash = OperatingSystem.IsWindows() ? "the Recycle Bin" : "the trash";
        if (!await Dialogs.ConfirmAsync("Delete the old file?",
                $"{relative.Trim()} is not used by the card any more. Move it to {trash}?",
                $"Move to {trash}", "Keep the file"))
            return;

        try
        {
            Trash.MoveToTrash(full);
            FilesChanged();
        }
        catch (Exception exception)
        {
            Log.Error($"Could not remove '{full}'", exception);
            await Dialogs.ShowErrorAsync("Could not remove the file", exception.Message);
        }
    }

    /// <summary>Where a picked file goes in the card, by kind (layout of the retrogc-gamecards cards).</summary>
    public string DefaultFolder(PathKind kind, string file)
    {
        var editor = Editor;
        var romFolder = editor?.RetroArchRoms.Values.Select(Path.GetDirectoryName).FirstOrDefault(d => !string.IsNullOrEmpty(d))
                        ?.Replace('\\', '/');
        var gamesFolder = romFolder ?? $"games/{PlatformFolder(editor)}";
        var isMra = file.EndsWith(".mra", StringComparison.OrdinalIgnoreCase);
        var arcade = isMra || (editor is not null && PathEntryViewModel.AnyMra(editor.MisterRoms));

        return kind switch
        {
            PathKind.Image => string.Empty,
            PathKind.RetroArchRom or PathKind.RetroArchRuntime => gamesFolder,
            PathKind.MisterCore => arcade ? "_Arcade/cores" : "_Console",
            PathKind.MisterRom => isMra ? "_Arcade" : gamesFolder,
            PathKind.MisterRuntime => gamesFolder,
            _ => string.Empty
        };
    }

    private static string PlatformFolder(CardEditorViewModel? editor)
    {
        var name = editor?.System is { Length: > 0 } system ? system : editor?.Platform ?? string.Empty;
        var clean = new string(name.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return clean.Length > 0 ? clean : "roms";
    }
}

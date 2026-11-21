using System;
using System.IO;
using System.Linq;

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

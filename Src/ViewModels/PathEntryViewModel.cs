using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroGcCreator.Services;

namespace RetroGcCreator.ViewModels;

/// <summary>What a path points to, so file pickers can suggest a folder and file types.</summary>
public enum PathKind
{
    Image,
    RetroArchRom,
    RetroArchRuntime,
    MisterCore,
    MisterRom,
    MisterRuntime
}

/// <summary>One card relative path (cover, ROM, BIOS, core) with its on-disk status.</summary>
public sealed partial class PathEntryViewModel : ObservableObject
{
    private readonly CardContext _context;

    public PathEntryViewModel(CardContext context, PathKind kind, string path = "", PathListViewModel? owner = null)
    {
        _context = context;
        Kind = kind;
        Owner = owner;
        Path = path;
    }

    public PathKind Kind { get; }
    public PathListViewModel? Owner { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(IsPresent), nameof(IsMissing), nameof(IsInvalid), nameof(StatusText))]
    public partial string Path { get; set; } = string.Empty;

    partial void OnPathChanged(string value) => _context.MarkChanged();

    public PathState State => CardFolder.Check(_context.Root, Path);
    public bool IsPresent => State == PathState.Present;
    public bool IsMissing => State == PathState.Missing;
    public bool IsInvalid => State == PathState.Invalid;

    public string StatusText => State switch
    {
        PathState.Present => "Found in the card",
        PathState.Missing => "Missing: choose the file to copy it into the card",
        PathState.Invalid => "Invalid path: must be relative, inside the card, with FAT32-safe names",
        _ => string.Empty
    };

    /// <summary>Re-checks the file (after an import or a folder change).</summary>
    public void Refresh()
    {
        // The status properties are computed from the disk: announce them all, not only the path.
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsPresent));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(IsInvalid));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// Picks a file and puts it into the card:
    /// - missing file, or a BIOS / system file (cores look for its exact name): copied to the current path;
    /// - present ROM / cover / core: the picked file replaces it with its own name, next to the old one
    ///   (then the old file can be moved to the Recycle Bin if nothing else uses it);
    /// - no path yet: copied to the default folder for this kind.
    /// A file already inside the card is used where it is.
    /// </summary>
    [RelayCommand]
    private async Task BrowseAsync()
    {
        var files = await _context.Dialogs.PickFilesAsync(Title(Kind), multiple: false);
        if (files.Count == 0)
            return;

        var picked = files[0];
        var oldPath = Path.Trim();
        var oldState = State;
        var outside = CardFolder.RelativeTo(_context.Root, picked) is null;
        try
        {
            if (outside && oldState == PathState.Missing ||
                outside && oldState == PathState.Present && KeepsName(Kind))
            {
                CardFolder.ImportAs(_context.Root, picked, oldPath);
                Refresh();
            }
            else
            {
                var folder = oldState == PathState.Present
                    ? System.IO.Path.GetDirectoryName(oldPath.TrimStart('/'))?.Replace('\\', '/') ?? string.Empty
                    : _context.DefaultFolder(Kind, picked);
                Path = CardFolder.Import(_context.Root, picked, folder);
            }

            _context.FilesChanged();
        }
        catch (Exception exception)
        {
            Log.Error("Import", exception);
            await _context.Dialogs.ShowErrorAsync("Could not add the file", exception.Message);
            return;
        }

        if (oldState == PathState.Present && !string.Equals(oldPath, Path.Trim(), StringComparison.Ordinal))
            await _context.OfferDeleteIfUnusedAsync(oldPath);
        else if (oldState == PathState.Missing)
            // A missing file was just filled: other files may now be leftovers (e.g. an older ROM).
            await _context.CheckUnusedFilesAsync();
    }

    /// <summary>BIOS / system files are looked for by name by the cores: replacing one keeps its name.</summary>
    private static bool KeepsName(PathKind kind) => kind is PathKind.RetroArchRuntime or PathKind.MisterRuntime;

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Owner is not null)
            await Owner.RemoveAsync(this);
    }

    [RelayCommand]
    private void MoveUp() => Owner?.Move(this, -1);

    [RelayCommand]
    private void MoveDown() => Owner?.Move(this, +1);

    public static string Title(PathKind kind) => kind switch
    {
        PathKind.Image => "Choose the cover image",
        PathKind.RetroArchRom or PathKind.MisterRom => "Choose the game file",
        PathKind.MisterCore => "Choose the MiSTer core (.rbf)",
        _ => "Choose the system / BIOS file"
    };

    public static bool IsMra(string path) =>
        path.EndsWith(".mra", StringComparison.OrdinalIgnoreCase);

    public static bool AnyMra(PathListViewModel list) => list.Items.Any(i => IsMra(i.Path));
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroGcCreator.Services;

namespace RetroGcCreator.ViewModels;

/// <summary>An ordered list of card paths (roms, runtimes).</summary>
public sealed partial class PathListViewModel : ObservableObject
{
    private readonly CardContext _context;

    public PathListViewModel(CardContext context, PathKind kind, string title, string hint)
    {
        _context = context;
        Kind = kind;
        Title = title;
        Hint = hint;
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            _context.MarkChanged();
        };
    }

    public PathKind Kind { get; }
    public string Title { get; }
    public string Hint { get; }

    public ObservableCollection<PathEntryViewModel> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public IEnumerable<string> Values => Items.Select(i => i.Path.Trim()).Where(p => p.Length > 0);

    public void Set(IEnumerable<string> values)
    {
        Items.Clear();
        foreach (var value in values)
            Items.Add(new PathEntryViewModel(_context, Kind, value, this));
    }

    /// <summary>Adds files: each is copied into the card (default folder) unless already inside it.</summary>
    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var files = await _context.Dialogs.PickFilesAsync(PathEntryViewModel.Title(Kind), multiple: true);
        try
        {
            foreach (var file in files)
            {
                var relative = CardFolder.Import(_context.Root, file, _context.DefaultFolder(Kind, file));
                if (Items.All(i => i.Path != relative))
                    Items.Add(new PathEntryViewModel(_context, Kind, relative, this));
            }

            if (files.Count > 0)
                _context.FilesChanged();
        }
        catch (Exception exception)
        {
            Log.Error("Import", exception);
            await _context.Dialogs.ShowErrorAsync("Could not add the file", exception.Message);
        }
    }

    /// <summary>Adds an empty line to type a path by hand.</summary>
    [RelayCommand]
    private void AddPath() => Items.Add(new PathEntryViewModel(_context, Kind, string.Empty, this));

    /// <summary>
    /// Removes a line; when its file is in the card and no other line of the card uses it any more,
    /// offers to move the file to the Recycle Bin too.
    /// </summary>
    public async Task RemoveAsync(PathEntryViewModel item)
    {
        var path = item.Path.Trim();
        var wasPresent = item.IsPresent;
        if (!Items.Remove(item))
            return;

        if (!wasPresent || _context.Editor is not { } editor || _context.Root.Length == 0)
            return;

        // Still used elsewhere (e.g. the same ROM for RetroArch and MiSTer): the file stays.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = CardFolder.Resolve(_context.Root, path.TrimStart('/'));
        if (full is null || editor.AllPaths().Any(p =>
                CardFolder.Resolve(_context.Root, p.Entry.Path.Trim().TrimStart('/')) is { } other &&
                string.Equals(other, full, comparison)))
            return;

        var trash = OperatingSystem.IsWindows() ? "the Recycle Bin" : "the trash";
        if (!await _context.Dialogs.ConfirmAsync("Delete the file too?",
                $"{path} is not used by the card any more. Also move it to {trash}?",
                $"Move to {trash}", "Keep the file"))
            return;

        try
        {
            Trash.MoveToTrash(full);
            _context.FilesChanged();
        }
        catch (Exception exception)
        {
            Log.Error($"Could not remove '{full}'", exception);
            await _context.Dialogs.ShowErrorAsync("Could not remove the file", exception.Message);
        }
    }

    public void Move(PathEntryViewModel item, int offset)
    {
        var index = Items.IndexOf(item);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Items.Count)
            return;
        Items.Move(index, target);
    }

    public void Refresh()
    {
        foreach (var item in Items)
            item.Refresh();
    }
}

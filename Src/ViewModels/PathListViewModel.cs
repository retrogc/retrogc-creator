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

    public void Remove(PathEntryViewModel item) => Items.Remove(item);

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

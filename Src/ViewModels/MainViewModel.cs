using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroGcCreator.Models;
using RetroGcCreator.Services;

namespace RetroGcCreator.ViewModels;

/// <summary>One line of the "files" checklist.</summary>
public sealed record FileCheck(string Label, string Path, PathState State)
{
    public bool IsPresent => State == PathState.Present;
    public bool IsProblem => State is PathState.Missing or PathState.Invalid;
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDialogs _dialogs;
    private readonly CardContext _context;
    private CardDocument? _document;
    private CancellationTokenSource? _busyCts;

    /// <summary>Temporary folder of a card image opened with "Just view" (deleted when closed).</summary>
    private string? _viewFolder;

    private static string ViewRoot => Path.Combine(Path.GetTempPath(), "retrogc-creator");

    public MainViewModel(IDialogs dialogs)
    {
        _dialogs = dialogs;
        // Any edit (field, path, list) marks the card dirty and refreshes the checklist.
        _context = new CardContext(dialogs, () =>
        {
            IsDirty = true;
            RefreshChecks();
        }, () =>
        {
            // A file added for one line can also be another line's (same ROM for RetroArch and MiSTer).
            Editor?.RefreshFiles();
            RefreshChecks();
        });
        _context.UnusedFilesCheck = () => IsReadOnly ? Task.CompletedTask : OfferCleanupAsync();
        Editor = new CardEditorViewModel(_context);
        Editor.Image.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PathEntryViewModel.Path))
                UpdateCover();
        };
    }

    public CardEditorViewModel Editor { get; }

    // ------------------------------------------------------------ state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCard), nameof(WindowTitle), nameof(CardFolderName))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(BuildCommand), nameof(FlashCommand), nameof(OpenCardFolderCommand),
        nameof(ReloadFilesCommand), nameof(DuplicateCommand), nameof(ScrapeCommand))]
    public partial string CardRoot { get; set; } = string.Empty;

    /// <summary>A card image opened with "Just view": nothing can be saved, "Extract…" makes an editable copy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(IsEditable))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(BuildCommand), nameof(ExtractCommand), nameof(ScrapeCommand))]
    public partial bool IsReadOnly { get; set; }

    public bool IsEditable => !IsReadOnly;

    /// <summary>The image being viewed (read-only mode).</summary>
    [ObservableProperty] public partial string ViewedImage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(DefinitionName))]
    public partial string DefinitionPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial bool IsDirty { get; set; }

    public bool HasCard => CardRoot.Length > 0;
    public string CardFolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(CardRoot));
    public string DefinitionName => Path.GetFileName(DefinitionPath);

    public string WindowTitle => !HasCard
        ? "Retro Game Card Creator"
        : IsReadOnly
            ? $"{Path.GetFileName(ViewedImage)} (read-only) | Retro Game Card Creator"
            : $"{(IsDirty ? "● " : string.Empty)}{CardFolderName} | Retro Game Card Creator";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    public partial Bitmap? Cover { get; set; }

    public bool HasCover => Cover is not null;

    public ObservableCollection<FileCheck> Checks { get; } = [];

    [ObservableProperty] public partial string ChecksSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasProblems { get; set; }

    // ------------------------------------------------------------ build

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(FlashCommand), nameof(SaveCommand),
        nameof(NewCardCommand), nameof(OpenFolderCommand),
        nameof(OpenImageCommand), nameof(ExtractCommand), nameof(DuplicateCommand), nameof(ScrapeCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial double BusyPercent { get; set; }
    [ObservableProperty] public partial string BusyStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string LastImage { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusMessage { get; set; } = "Open a card folder or create a new card.";
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    // ------------------------------------------------------------ open / new / save

    private bool CanChangeCard() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanChangeCard))]
    private async Task NewCardAsync()
    {
        if (!await ConfirmDiscardAsync())
            return;

        var folder = await _dialogs.PickFolderAsync("Choose a folder for the new card (an empty one)");
        if (folder is null)
            return;

        if (CardFolder.FindDefinition(folder) is { } existing)
        {
            if (!await _dialogs.ConfirmAsync("This folder already has a card",
                    $"{Path.GetFileName(existing)} is already there. Open it instead?", "Open it"))
                return;
            Open(existing);
            return;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var definition = Path.Combine(folder, Sanitize(name) + ".rgc");
        _document = CardDocument.CreateNew(name);
        LoadDocument(folder, definition, _document);
        IsDirty = true;
        SetStatus($"New card in {folder}. Fill in the fields, add the game files, then save.");
    }

    [RelayCommand(CanExecute = nameof(CanChangeCard))]
    private async Task OpenFolderAsync()
    {
        if (!await ConfirmDiscardAsync())
            return;

        var folder = await _dialogs.PickFolderAsync("Open a card folder");
        if (folder is null)
            return;

        var definition = CardFolder.FindDefinition(folder);
        if (definition is null)
        {
            await _dialogs.ShowErrorAsync("No card in this folder",
                "There is no .rgc file at the root of this folder. Use \"New card\" to create one.");
            return;
        }

        Open(definition);
    }

    /// <summary>Opens a card definition; its folder is the card root.</summary>
    public void Open(string definition) => Open(definition, viewedImage: null);

    private bool Open(string definition, string? viewedImage)
    {
        try
        {
            var document = CardDocument.Load(definition);
            _document = document;
            LoadDocument(Path.GetDirectoryName(Path.GetFullPath(definition))!, Path.GetFullPath(definition), document);

            IsReadOnly = viewedImage is not null;
            ViewedImage = viewedImage ?? string.Empty;
            if (viewedImage is not null)
            {
                // "Write to USB" flashes the viewed image as is.
                LastImage = viewedImage;
                SetStatus($"Viewing {viewedImage} (read-only). Use \"Extract…\" to edit it.");
            }
            else
            {
                SetStatus($"Opened {definition}");
                // Files the card doesn't use would end up in the image: offer to remove them.
                _ = OfferCleanupAsync();
            }

            return true;
        }
        catch (Exception exception)
        {
            Log.Error($"Open '{definition}'", exception);
            SetStatus($"Could not open {Path.GetFileName(definition)}: {exception.Message}", true);
            return false;
        }
    }

    /// <summary>
    /// Lists the files / folders of the card folder that the definition doesn't use and offers to move
    /// them to the Recycle Bin.
    /// </summary>
    private async Task OfferCleanupAsync()
    {
        try
        {
            var root = CardRoot;
            var definition = DefinitionPath;
            var referenced = Editor.AllPaths().Select(p => p.Entry.Path).ToList();
            var extra = await Task.Run(() => CardFolder.FindUnreferenced(root, definition, referenced));
            if (extra.Count == 0 || CardRoot != root)
                return;

            var name = Path.GetFileName(definition);
            var trash = OperatingSystem.IsWindows() ? "the Recycle Bin" : "the trash";
            var plural = extra.Count > 1 ? "These files are" : "This file is";
            if (!await _dialogs.ConfirmListAsync("Files not used by the card",
                    $"{plural} in the card folder but not in {name}: they would end up in the card image. " +
                    $"Move {(extra.Count > 1 ? "them" : "it")} to {trash}?",
                    extra.Select(e => e.Display).ToList(), $"Move to {trash}", "Keep"))
            {
                SetStatus($"Kept {extra.Count} file{(extra.Count > 1 ? "s" : "")} not used by the card");
                return;
            }

            var failed = new List<string>();
            foreach (var item in extra)
            {
                try
                {
                    Trash.MoveToTrash(item.FullPath);
                }
                catch (Exception exception)
                {
                    Log.Error($"Could not remove '{item.FullPath}'", exception);
                    failed.Add($"{item.Display}: {exception.Message}");
                }
            }

            Editor.RefreshFiles();
            RefreshChecks();
            if (failed.Count > 0)
                await _dialogs.ShowErrorAsync("Some files could not be removed", string.Join(Environment.NewLine, failed));
            else
                SetStatus($"Moved {extra.Count} item{(extra.Count > 1 ? "s" : "")} not used by the card to {trash}");
        }
        catch (Exception exception)
        {
            Log.Error("Card folder cleanup", exception);
            SetStatus($"Could not check the card folder: {exception.Message}", true);
        }
    }

    /// <summary>
    /// Files changed on disk (cover replaced...): reload the cover, re-check every path, and look again
    /// for files the card doesn't use (e.g. a ROM that was replaced by another one).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasCard))]
    private async Task ReloadFilesAsync()
    {
        Editor.RefreshFiles();
        UpdateCover();
        RefreshChecks();
        SetStatus("Files reloaded");

        if (!IsReadOnly)
            await OfferCleanupAsync();
    }

    private void LoadDocument(string root, string definition, CardDocument document)
    {
        // Leaving a viewed image: its temporary folder goes away.
        if (_viewFolder is not null && !IsInside(root, _viewFolder))
            DeleteViewFolder();

        IsReadOnly = false;
        ViewedImage = string.Empty;
        _context.Loading = true;
        try
        {
            _context.Root = root;
            CardRoot = root;
            DefinitionPath = definition;
            Editor.Load(document);
            Editor.RefreshFiles();
        }
        finally
        {
            _context.Loading = false;
        }

        IsDirty = false;
        LastImage = string.Empty;
        UpdateCover();
        RefreshChecks();
    }

    private bool CanSave() => HasCard && !IsBusy && !IsReadOnly;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => TrySave();

    private bool TrySave()
    {
        if (_document is null)
            return false;

        try
        {
            Editor.Apply(_document);
            _document.Save(DefinitionPath);
            IsDirty = false;
            SetStatus($"Saved {DefinitionPath}");
            return true;
        }
        catch (Exception exception)
        {
            Log.Error("Save", exception);
            SetStatus($"Could not save: {exception.Message}", true);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasCard))]
    private void OpenCardFolder() => OpenInFileManager(CardRoot);

    /// <summary>Asks before dropping unsaved changes. Returns false to keep the current card.</summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!HasCard || !IsDirty)
            return true;

        return await _dialogs.ConfirmAsync("Unsaved changes",
            $"{DefinitionName} has unsaved changes. Discard them?", "Discard");
    }

    // ------------------------------------------------------------ card images

    [RelayCommand(CanExecute = nameof(CanChangeCard))]
    private async Task OpenImageAsync()
    {
        if (!await ConfirmDiscardAsync())
            return;

        var files = await _dialogs.PickFilesAsync("Open a card image", false, null, "Card image", ["*.img.xz", "*.img"]);
        if (files.Count == 0)
            return;

        var image = files[0];
        var choice = await _dialogs.ChooseAsync("Open a card image",
            $"{Path.GetFileName(image)}\n\nExtract it to a folder to edit and rebuild it, or just view it " +
            "(unpacked to a temporary folder, read-only).",
            ["Extract to a folder…", "Just view", "Cancel"]);

        if (choice == 0)
        {
            var destination = await PickExtractDestinationAsync(image);
            if (destination is not null && await ExtractImageAsync(image, destination))
                await OpenExtractedAsync(destination, viewedImage: null);
        }
        else if (choice == 1)
        {
            await ViewImageAsync(image);
        }
    }

    /// <summary>Opens a card image read-only (unpacked to a temporary folder).</summary>
    public async Task ViewImageAsync(string image)
    {
        var temporary = Path.Combine(ViewRoot, Guid.NewGuid().ToString("N"));
        if (!await ExtractImageAsync(image, temporary))
        {
            TryDeleteFolder(temporary);
            return;
        }

        DeleteViewFolder();
        _viewFolder = temporary;
        if (!await OpenExtractedAsync(temporary, image))
            DeleteViewFolder();
    }

    /// <summary>True for a card image file (.img.xz or .img), as opposed to a card definition.</summary>
    public static bool IsImageFile(string path) =>
        path.EndsWith(".xz", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".img", StringComparison.OrdinalIgnoreCase);

    private bool CanExtract() => IsReadOnly && !IsBusy;

    /// <summary>Read-only mode: copies the viewed card to a folder and opens it there, editable.</summary>
    [RelayCommand(CanExecute = nameof(CanExtract))]
    private async Task ExtractAsync()
    {
        var destination = await PickExtractDestinationAsync(ViewedImage);
        if (destination is null)
            return;

        try
        {
            CopyFolder(CardRoot, destination);
            var definition = Path.Combine(destination, Path.GetRelativePath(CardRoot, DefinitionPath));
            Open(definition);
            SetStatus($"Extracted to {destination}");
        }
        catch (Exception exception)
        {
            Log.Error("Extract", exception);
            await _dialogs.ShowErrorAsync("Could not extract the card", exception.Message);
        }
    }

    /// <summary>Asks for a parent folder; the card goes into a new sub folder named after the image.</summary>
    private bool CanDuplicate() => HasCard && !IsBusy;

    /// <summary>
    /// Copies the open card (folder, .rgc or viewed image) into a chosen empty folder, which becomes the
    /// new card's root, then opens the copy. Unsaved edits go to the copy, the original stays as saved.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private async Task DuplicateAsync()
    {
        if (_document is null)
            return;

        var source = CardRoot;
        var start = IsReadOnly && ViewedImage.Length > 0
            ? Path.GetDirectoryName(ViewedImage)
            : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source));
        var destination = await _dialogs.PickFolderAsync("Duplicate the card into… (an empty folder: the new card's root)", start);
        if (destination is null)
            return;

        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (IsInside(destination, source))
        {
            await _dialogs.ShowErrorAsync("Choose another folder",
                "The copy can't go into the card folder itself. Choose (or create) a folder outside of it.");
            return;
        }

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            await _dialogs.ShowErrorAsync("The folder isn't empty",
                $"{destination} isn't empty. Choose (or create) an empty folder: it becomes the new card's root.");
            return;
        }

        // The current state of the editor, unsaved edits included, goes to the copy.
        Editor.Apply(_document);
        var oldDefinition = Path.Combine(destination, Path.GetRelativePath(source, DefinitionPath));
        var definition = Path.Combine(destination, Sanitize(Path.GetFileName(destination)) + ".rgc");

        IsBusy = true;
        BusyPercent = 0;
        BusyStatus = "Copying the card…";
        try
        {
            await Task.Run(() => CopyFolder(source, destination, new Progress<double>(f => BusyPercent = f * 100)));
            _document.Save(definition);
            if (!string.Equals(oldDefinition, definition, StringComparison.OrdinalIgnoreCase) && File.Exists(oldDefinition))
                File.Delete(oldDefinition);
            RenameCover(destination, definition);
        }
        catch (Exception exception)
        {
            Log.Error("Duplicate", exception);
            await _dialogs.ShowErrorAsync("Could not duplicate the card", exception.Message);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (Open(definition, viewedImage: null))
            SetStatus($"Duplicated to {destination}");
    }

    /// <summary>
    /// The duplicated card's cover is named after its folder too, like its definition
    /// (<c>smb.png</c> → <c>super-mario-bros-3.png</c>, same folder and extension).
    /// </summary>
    private static void RenameCover(string root, string definition)
    {
        var card = CardDocument.Load(definition);
        var image = card.GetString("image").Trim().TrimStart('/');
        if (image.Length == 0 || CardFolder.Resolve(root, image) is not { } current || !File.Exists(current))
            return;

        var folder = Path.GetDirectoryName(image)?.Replace('\\', '/');
        var renamed = (string.IsNullOrEmpty(folder) ? string.Empty : folder + "/") +
                      Path.GetFileNameWithoutExtension(definition) + Path.GetExtension(image);
        if (CardFolder.Resolve(root, renamed) is not { } target ||
            string.Equals(current, target, StringComparison.OrdinalIgnoreCase) || File.Exists(target))
            return;

        File.Move(current, target);
        card.SetString("image", renamed);
        card.Save(definition);
        Log.Info($"Cover renamed: {image} -> {renamed}");
    }

    private async Task<string?> PickExtractDestinationAsync(string image)
    {
        var parent = await _dialogs.PickFolderAsync("Extract the card into…", Path.GetDirectoryName(image));
        if (parent is null)
            return null;

        var name = Path.GetFileName(image);
        foreach (var suffix in new[] { ".img.xz", ".xz", ".img" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        var destination = Path.Combine(parent, Sanitize(name));
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            await _dialogs.ShowErrorAsync("The folder isn't empty",
                $"{destination} already exists and isn't empty. Choose another place, or remove it first.");
            return null;
        }

        return destination;
    }

    private async Task<bool> ExtractImageAsync(string image, string destination)
    {
        IsBusy = true;
        BusyPercent = 0;
        BusyStatus = "Reading the image…";
        _busyCts = new CancellationTokenSource();
        try
        {
            await ImageExtractor.ExtractAsync(image, destination, new Progress<ExtractProgress>(p =>
            {
                var xz = Xz.IsXz(image);
                BusyPercent = p.Stage == ExtractStage.Decompressing ? p.Fraction * 60 : (xz ? 60 : 0) + p.Fraction * (xz ? 40 : 100);
                BusyStatus = p.Stage == ExtractStage.Decompressing ? "Decompressing…" : "Extracting files…";
            }), _busyCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
            return false;
        }
        catch (Exception exception)
        {
            Log.Error("Extract", exception);
            SetStatus($"Could not read the image: {exception.Message}", true);
            await _dialogs.ShowErrorAsync("Could not read the image", exception.Message);
            return false;
        }
        finally
        {
            _busyCts.Dispose();
            _busyCts = null;
            IsBusy = false;
        }
    }

    private async Task<bool> OpenExtractedAsync(string folder, string? viewedImage)
    {
        var definition = CardFolder.FindDefinition(folder);
        if (definition is not null)
            return Open(definition, viewedImage);

        var installer = File.Exists(Path.Combine(folder, CardFolder.InstallerManifest));
        await _dialogs.ShowErrorAsync("No game card in this image",
            installer
                ? "This is an installer card (retrogc-data.rgc), not a game card."
                : "There is no .rgc card definition at the root of this image." +
                  (viewedImage is null ? $"\n\nIts files were extracted to {folder}." : string.Empty));
        return false;
    }

    /// <summary>Removes "Just view" folders left by a previous run (crash, kill).</summary>
    public static void CleanupStaleViews()
    {
        try
        {
            if (!Directory.Exists(ViewRoot))
                return;

            foreach (var folder in Directory.EnumerateDirectories(ViewRoot))
            {
                if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-1))
                    TryDeleteFolder(folder);
            }
        }
        catch (Exception exception)
        {
            Log.Error("Cleanup of temporary folders", exception);
        }
    }

    private void DeleteViewFolder()
    {
        if (_viewFolder is null)
            return;

        TryDeleteFolder(_viewFolder);
        _viewFolder = null;
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception)
        {
            Log.Error($"Could not delete '{folder}'", exception);
        }
    }

    private static void CopyFolder(string source, string destination, IProgress<double>? progress = null)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        Directory.CreateDirectory(destination);

        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        var total = Math.Max(1, files.Sum(f => f.Length));
        long done = 0;
        foreach (var file in files)
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file.FullName));
            File.Copy(file.FullName, target, overwrite: false);
            File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
            done += file.Length;
            progress?.Report((double)done / total);
        }
    }

    private static bool IsInside(string path, string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ screenscraper

    private bool CanScrape() => HasCard && !IsBusy && !IsReadOnly;

    /// <summary>Opens the ScreenScraper window, then puts the chosen fields and picture in the card.</summary>
    [RelayCommand(CanExecute = nameof(CanScrape))]
    private async Task ScrapeAsync()
    {
        var current = new Dictionary<ScrapeFieldKind, string>
        {
            [ScrapeFieldKind.Name] = Editor.Name,
            [ScrapeFieldKind.Description] = Editor.Description,
            [ScrapeFieldKind.Platform] = Editor.Platform,
            [ScrapeFieldKind.Year] = Editor.Year,
            [ScrapeFieldKind.Developer] = Editor.Developer,
            [ScrapeFieldKind.Genre] = Editor.Genre,
            [ScrapeFieldKind.Players] = Editor.Players
        };
        var rom = Editor.RetroArchRoms.Items.FirstOrDefault(r => r.State == PathState.Present) is { } entry
            ? CardFolder.Resolve(CardRoot, entry.Path.Trim().TrimStart('/'))
            : null;

        using var viewModel = new ScraperViewModel(new ScrapeRequest(current, Editor.System, rom,
            Editor.Image.State == PathState.Present));
        var selection = await _dialogs.ShowScraperAsync(viewModel);
        if (selection is null)
            return;

        foreach (var (kind, value) in selection.Fields)
        {
            switch (kind)
            {
                case ScrapeFieldKind.Name: Editor.Name = value; break;
                case ScrapeFieldKind.Description: Editor.Description = value; break;
                case ScrapeFieldKind.Platform: Editor.Platform = value; break;
                case ScrapeFieldKind.Year: Editor.Year = value; break;
                case ScrapeFieldKind.Developer: Editor.Developer = value; break;
                case ScrapeFieldKind.Genre: Editor.Genre = value; break;
                case ScrapeFieldKind.Players: Editor.Players = value; break;
            }
        }

        var cover = selection.Cover is not null && await SaveScrapedCoverAsync(selection.Cover, selection.CoverLabel);
        var parts = selection.Fields.Count > 0
            ? $"{selection.Fields.Count} field{(selection.Fields.Count > 1 ? "s" : string.Empty)}"
            : string.Empty;
        if (cover)
            parts += (parts.Length > 0 ? " and " : string.Empty) + "the cover";
        if (parts.Length > 0)
            SetStatus($"Filled from ScreenScraper ({selection.GameName}): {parts}." +
                      (selection.Fields.Count > 0 ? " Save the card to keep them." : string.Empty));
    }

    /// <summary>
    /// Writes the picture as the card cover, as PNG: over the current cover when it is a PNG, otherwise as
    /// <c>&lt;definition name&gt;.png</c> at the card root (like a duplicated card's cover).
    /// </summary>
    private async Task<bool> SaveScrapedCoverAsync(byte[] picture, string label)
    {
        var old = Editor.Image.Path.Trim();
        var current = old.TrimStart('/');
        var relative = current.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                       CardFolder.Check(CardRoot, current) is PathState.Present or PathState.Missing
            ? current
            : Path.GetFileNameWithoutExtension(DefinitionPath) + ".png";
        var target = CardFolder.Resolve(CardRoot, relative);
        if (target is null)
            return false;

        if (File.Exists(target) && !await _dialogs.ConfirmAsync("Replace the cover?",
                $"{relative} is already in the card. Replace it with the ScreenScraper picture ({label})?", "Replace"))
            return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".tmp";
            using (var bitmap = new Bitmap(new MemoryStream(picture)))
            using (var stream = File.Create(temporary))
                bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception exception)
        {
            Log.Error("ScreenScraper cover", exception);
            await _dialogs.ShowErrorAsync("Could not save the cover", exception.Message);
            return false;
        }

        if (string.Equals(old, relative, StringComparison.Ordinal))
        {
            Editor.Image.Refresh();
            UpdateCover();
        }
        else
        {
            // Changing the path reloads the sidebar cover and marks the card changed.
            Editor.Image.Path = relative;
        }

        _context.FilesChanged();
        if (old.Length > 0 && !string.Equals(old, relative, StringComparison.Ordinal))
            await _context.OfferDeleteIfUnusedAsync(old);
        return true;
    }

    // ------------------------------------------------------------ build / flash

    private bool CanBuild() => HasCard && !IsBusy && !IsReadOnly;

    private bool CanFlash() => HasCard && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync() => await BuildImageAsync(askPath: true);

    [RelayCommand(CanExecute = nameof(CanFlash))]
    private async Task FlashAsync()
    {
        var image = LastImage.Length > 0 && File.Exists(LastImage) && !IsDirty
            ? LastImage
            : await BuildImageAsync(askPath: true);
        if (image is not null)
            await _dialogs.ShowFlashAsync(image);
    }

    [RelayCommand]
    private void CancelBusy() => _busyCts?.Cancel();

    /// <summary>Saves, validates and builds the image. Returns its path, or null.</summary>
    private async Task<string?> BuildImageAsync(bool askPath)
    {
        var problems = Editor.Validate();
        if (problems.Count > 0)
        {
            await _dialogs.ShowErrorAsync("The card isn't ready",
                string.Join(Environment.NewLine, problems.Take(12).Select(p => "• " + p)));
            return null;
        }

        if (IsDirty && !TrySave())
            return null;

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(CardRoot)) ?? CardRoot;
        var suggested = $"{ImageName()}.img.xz";
        var output = askPath ? await _dialogs.PickSaveFileAsync("Save the card image", suggested, parent) : Path.Combine(parent, suggested);
        if (output is null)
            return null;
        if (!output.EndsWith(".img.xz", StringComparison.OrdinalIgnoreCase))
            output += output.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? ".xz" : ".img.xz";

        IsBusy = true;
        BusyPercent = 0;
        BusyStatus = "Writing files…";
        _busyCts = new CancellationTokenSource();
        try
        {
            await ImageBuilder.BuildAsync(CardRoot, output, new Progress<BuildProgress>(p =>
            {
                // Writing ≈ 30%, compressing ≈ 70% of the time.
                BusyPercent = p.Stage == BuildStage.Writing ? p.Fraction * 30 : 30 + p.Fraction * 70;
                BusyStatus = p.Stage == BuildStage.Writing ? "Writing files…" : "Compressing (xz)…";
            }), _busyCts.Token);

            LastImage = output;
            SetStatus($"Image ready: {output} ({UsbDrive.FormatSize(new FileInfo(output).Length)})");
            return output;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Image build cancelled.");
            return null;
        }
        catch (Exception exception)
        {
            Log.Error("Build", exception);
            SetStatus($"Image build failed: {exception.Message}", true);
            return null;
        }
        finally
        {
            _busyCts.Dispose();
            _busyCts = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowImage()
    {
        if (LastImage.Length > 0)
            OpenInFileManager(Path.GetDirectoryName(LastImage)!);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Suggested image name: the card folder name (an extracted image keeps its original name).</summary>
    private string ImageName() => Sanitize(CardFolderName);

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray()).Trim('-', '.');
        return clean.Length > 0 ? clean : "card";
    }

    private void RefreshChecks()
    {
        if (!HasCard)
            return;

        Checks.Clear();
        foreach (var (label, entry) in Editor.AllPaths().Where(p => p.Entry.State != PathState.Empty))
            Checks.Add(new FileCheck(label, entry.Path, entry.State));

        var problems = Editor.Validate();
        HasProblems = problems.Count > 0;
        ChecksSummary = HasProblems
            ? $"{problems.Count} thing{(problems.Count > 1 ? "s" : string.Empty)} to fix before building"
            : "Ready to build";
    }

    private void UpdateCover()
    {
        var previous = Cover;
        Cover = null;
        previous?.Dispose();

        if (Editor.Image.State != PathState.Present)
            return;

        try
        {
            var path = CardFolder.Resolve(CardRoot, Editor.Image.Path)!;
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            Cover = new Bitmap(stream);
        }
        catch (Exception exception)
        {
            Log.Error("Cover", exception);
        }
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusMessage = message;
        StatusIsError = error;
        if (error) Log.Error(message); else Log.Info(message);
    }

    private static void OpenInFileManager(string folder)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            Log.Error("Open folder", exception);
        }
    }

    public void Dispose()
    {
        _busyCts?.Cancel();
        DeleteViewFolder();
        Cover?.Dispose();
    }
}

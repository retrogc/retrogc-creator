using System;
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
    private CancellationTokenSource? _buildCts;

    public MainViewModel(IDialogs dialogs)
    {
        _dialogs = dialogs;
        // Any edit (field, path, list) marks the card dirty and refreshes the checklist.
        _context = new CardContext(dialogs, () =>
        {
            IsDirty = true;
            RefreshChecks();
        }, RefreshChecks);
        Editor = new CardEditorViewModel(_context);
        Editor.Image.PropertyChanged += (_, _) => UpdateCover();
    }

    public CardEditorViewModel Editor { get; }

    // ------------------------------------------------------------ state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCard), nameof(WindowTitle), nameof(CardFolderName))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(BuildCommand), nameof(FlashCommand), nameof(OpenCardFolderCommand))]
    public partial string CardRoot { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(DefinitionName))]
    public partial string DefinitionPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial bool IsDirty { get; set; }

    public bool HasCard => CardRoot.Length > 0;
    public string CardFolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(CardRoot));
    public string DefinitionName => Path.GetFileName(DefinitionPath);

    public string WindowTitle => HasCard
        ? $"{(IsDirty ? "● " : string.Empty)}{CardFolderName} — Retro Game Card Creator"
        : "Retro Game Card Creator";

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
        nameof(NewCardCommand), nameof(OpenFolderCommand), nameof(OpenDefinitionCommand))]
    public partial bool IsBuilding { get; set; }

    [ObservableProperty] public partial double BuildPercent { get; set; }
    [ObservableProperty] public partial string BuildStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string LastImage { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusMessage { get; set; } = "Open a card folder or create a new card.";
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    // ------------------------------------------------------------ open / new / save

    private bool CanChangeCard() => !IsBuilding;

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

    [RelayCommand(CanExecute = nameof(CanChangeCard))]
    private async Task OpenDefinitionAsync()
    {
        if (!await ConfirmDiscardAsync())
            return;

        var files = await _dialogs.PickFilesAsync("Open a .rgc card definition", false, null,
            "Retro Game Card", ["*.rgc"]);
        if (files.Count > 0)
            Open(files[0]);
    }

    /// <summary>Opens a card definition; its folder is the card root.</summary>
    public void Open(string definition)
    {
        try
        {
            var document = CardDocument.Load(definition);
            _document = document;
            LoadDocument(Path.GetDirectoryName(Path.GetFullPath(definition))!, Path.GetFullPath(definition), document);
            SetStatus($"Opened {definition}");
        }
        catch (Exception exception)
        {
            Log.Error($"Open '{definition}'", exception);
            SetStatus($"Could not open {Path.GetFileName(definition)}: {exception.Message}", true);
        }
    }

    private void LoadDocument(string root, string definition, CardDocument document)
    {
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

    private bool CanSave() => HasCard && !IsBuilding;

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

    // ------------------------------------------------------------ build / flash

    private bool CanBuild() => HasCard && !IsBuilding;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync() => await BuildImageAsync(askPath: true);

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task FlashAsync()
    {
        var image = LastImage.Length > 0 && File.Exists(LastImage) && !IsDirty
            ? LastImage
            : await BuildImageAsync(askPath: true);
        if (image is not null)
            await _dialogs.ShowFlashAsync(image);
    }

    [RelayCommand]
    private void CancelBuild() => _buildCts?.Cancel();

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

        IsBuilding = true;
        BuildPercent = 0;
        BuildStatus = "Writing files…";
        _buildCts = new CancellationTokenSource();
        try
        {
            await ImageBuilder.BuildAsync(CardRoot, output, new Progress<BuildProgress>(p =>
            {
                // Writing ≈ 30%, compressing ≈ 70% of the time.
                BuildPercent = p.Stage == BuildStage.Writing ? p.Fraction * 30 : 30 + p.Fraction * 70;
                BuildStatus = p.Stage == BuildStage.Writing ? "Writing files…" : "Compressing (xz)…";
            }), _buildCts.Token);

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
            _buildCts.Dispose();
            _buildCts = null;
            IsBuilding = false;
        }
    }

    [RelayCommand]
    private void ShowImage()
    {
        if (LastImage.Length > 0)
            OpenInFileManager(Path.GetDirectoryName(LastImage)!);
    }

    // ------------------------------------------------------------ helpers

    private string ImageName()
    {
        var platform = Sanitize((Editor.System.Length > 0 ? Editor.System : Editor.Platform).ToLowerInvariant());
        var folder = Sanitize(CardFolderName.ToLowerInvariant());
        return platform.Length > 0 && !folder.StartsWith(platform, StringComparison.Ordinal) ? $"{platform}-{folder}" : folder;
    }

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
        _buildCts?.Cancel();
        Cover?.Dispose();
    }
}

using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroGcCreator.Models;
using RetroGcCreator.Services;

namespace RetroGcCreator.ViewModels;

/// <summary>Every editable field of a card, loaded from / applied to a <see cref="CardDocument"/>.</summary>
public sealed partial class CardEditorViewModel : ObservableObject
{
    private const string Ra = CardDocument.RetroArch;
    private const string Mister = CardDocument.MisterFpga;

    private readonly CardContext _context;

    public CardEditorViewModel(CardContext context)
    {
        _context = context;
        context.Editor = this;

        Image = new PathEntryViewModel(context, PathKind.Image);
        Image.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PathEntryViewModel.Path))
                OnPropertyChanged(nameof(Image));
        };

        RetroArchRoms = new PathListViewModel(context, PathKind.RetroArchRom, "ROMs",
            "The first file is the one RetroArch starts; the others must exist (e.g. extra discs).");
        RetroArchRuntimes = new PathListViewModel(context, PathKind.RetroArchRuntime, "System / BIOS files",
            "Copied to RetroArch's system folder (Recalbox: /recalbox/share/bios) before the game starts.");
        MisterCore = new PathEntryViewModel(context, PathKind.MisterCore);
        MisterRoms = new PathListViewModel(context, PathKind.MisterRom, "ROMs",
            "The first file is loaded; arcade games point to a .mra file.");
        MisterRuntimes = new PathListViewModel(context, PathKind.MisterRuntime, "System files",
            "Extra files the core needs (Neo Geo BIOS, PSX boot ROMs...).");
    }

    // ------------------------------------------------------------ general

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Description { get; set; } = string.Empty;
    [ObservableProperty] public partial string Platform { get; set; } = string.Empty;
    [ObservableProperty] public partial string Year { get; set; } = string.Empty;
    [ObservableProperty] public partial string Developer { get; set; } = string.Empty;
    [ObservableProperty] public partial string Genre { get; set; } = string.Empty;
    [ObservableProperty] public partial string Players { get; set; } = string.Empty;

    public PathEntryViewModel Image { get; }

    // ------------------------------------------------------------ retroarch

    [ObservableProperty] public partial string Version { get; set; } = CardDocument.DefaultRetroArchVersion;
    [ObservableProperty] public partial string System { get; set; } = string.Empty;
    [ObservableProperty] public partial string Core { get; set; } = string.Empty;

    public PathListViewModel RetroArchRoms { get; }
    public PathListViewModel RetroArchRuntimes { get; }

    // ------------------------------------------------------------ misterfpga

    [ObservableProperty] public partial bool HasMister { get; set; }
    public PathEntryViewModel MisterCore { get; }
    public PathListViewModel MisterRoms { get; }
    public PathListViewModel MisterRuntimes { get; }
    [ObservableProperty] public partial string MisterDelay { get; set; } = string.Empty;
    [ObservableProperty] public partial string MisterIndex { get; set; } = string.Empty;
    [ObservableProperty] public partial string MisterType { get; set; } = string.Empty;

    /// <summary>Most cards load the same files on MiSTer as on RetroArch.</summary>
    [RelayCommand]
    private void CopyRetroArchRoms() => MisterRoms.Set(RetroArchRoms.Values);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(Image)))
            _context.MarkChanged();
    }

    // ------------------------------------------------------------ load / apply

    public void Load(CardDocument card)
    {
        Name = card.GetString("name");
        Description = card.GetString("description");
        Platform = card.GetString("platform");
        Year = card.GetString("year");
        Developer = card.GetString("developer");
        Genre = card.GetString("genre");
        Players = card.GetString("players");
        Image.Path = card.GetString("image");

        Version = card.GetString("version", Ra);
        System = card.GetString("system", Ra);
        Core = card.GetString("core", Ra);
        RetroArchRoms.Set(card.GetList("roms", Ra));
        RetroArchRuntimes.Set(card.GetList("runtimes", Ra));

        HasMister = card.HasSection(Mister);
        MisterCore.Path = card.GetString("core", Mister);
        MisterRoms.Set(card.GetList("roms", Mister));
        MisterRuntimes.Set(card.GetList("runtimes", Mister));
        MisterDelay = card.GetString("delay", Mister);
        MisterIndex = card.GetString("index", Mister);
        MisterType = card.GetString("type", Mister);
    }

    public void Apply(CardDocument card)
    {
        card.SetString("name", Name.Trim());
        card.SetString("description", Description.Trim());
        card.SetString("platform", Platform.Trim());
        card.SetString("year", Year.Trim());
        card.SetString("image", Image.Path.Trim());
        card.SetString("developer", Developer.Trim());
        card.SetString("genre", Genre.Trim());
        card.SetString("players", Players.Trim());

        card.SetString("version", Version.Trim(), Ra);
        card.SetString("system", System.Trim(), Ra);
        card.SetString("core", Core.Trim(), Ra);
        card.SetList("roms", Ra, RetroArchRoms.Values);
        card.SetList("runtimes", Ra, RetroArchRuntimes.Values);

        if (!HasMister)
        {
            card.RemoveSection(Mister);
            return;
        }

        card.SetString("core", MisterCore.Path.Trim(), Mister);
        card.SetList("roms", Mister, MisterRoms.Values);
        card.SetList("runtimes", Mister, MisterRuntimes.Values);
        card.SetString("delay", MisterDelay.Trim(), Mister);
        card.SetString("index", MisterIndex.Trim(), Mister);
        card.SetString("type", MisterType.Trim(), Mister);
    }

    /// <summary>Every referenced path with its label, for the checklist.</summary>
    public IEnumerable<(string Label, PathEntryViewModel Entry)> AllPaths()
    {
        if (!string.IsNullOrWhiteSpace(Image.Path))
            yield return ("Cover", Image);
        foreach (var e in RetroArchRoms.Items) yield return ("RetroArch ROM", e);
        foreach (var e in RetroArchRuntimes.Items) yield return ("RetroArch BIOS", e);
        if (!HasMister)
            yield break;
        if (!string.IsNullOrWhiteSpace(MisterCore.Path))
            yield return ("MiSTer core", MisterCore);
        foreach (var e in MisterRoms.Items) yield return ("MiSTer ROM", e);
        foreach (var e in MisterRuntimes.Items) yield return ("MiSTer system file", e);
    }

    /// <summary>Problems that block the image build (same required fields as the launcher).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) problems.Add("The card has no name.");
        if (string.IsNullOrWhiteSpace(Version)) problems.Add("RetroArch: the version is missing.");
        if (string.IsNullOrWhiteSpace(Core)) problems.Add("RetroArch: the core is missing.");
        if (!RetroArchRoms.Values.Any()) problems.Add("RetroArch: add at least one ROM.");
        if (HasMister && string.IsNullOrWhiteSpace(MisterCore.Path)) problems.Add("MiSTer: the core is missing.");
        if (HasMister && !MisterRoms.Values.Any()) problems.Add("MiSTer: add at least one ROM.");

        foreach (var (label, entry) in AllPaths())
        {
            if (entry.State == PathState.Missing) problems.Add($"{label} not found: {entry.Path}");
            if (entry.State == PathState.Invalid) problems.Add($"{label} has an invalid path: {entry.Path}");
        }

        return problems;
    }

    public void RefreshFiles()
    {
        Image.Refresh();
        MisterCore.Refresh();
        RetroArchRoms.Refresh();
        RetroArchRuntimes.Refresh();
        MisterRoms.Refresh();
        MisterRuntimes.Refresh();
    }
}

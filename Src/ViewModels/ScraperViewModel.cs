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
using RetroGcCreator.Services;
using RetroGcCreator.Services.ScreenScraper;

namespace RetroGcCreator.ViewModels;

public enum ScrapeFieldKind
{
    Name,
    Platform,
    Year,
    Developer,
    Genre,
    Players,
    Description
}

/// <summary>What the ScreenScraper window starts from: the card's current values and its first ROM.</summary>
public sealed record ScrapeRequest(
    IReadOnlyDictionary<ScrapeFieldKind, string> Current,
    string RecalboxSystem,
    string? RomPath,
    bool HasCover);

/// <summary>What the user chose to apply: the ticked fields, and the cover picture (or null).</summary>
public sealed record ScrapeSelection(
    IReadOnlyDictionary<ScrapeFieldKind, string> Fields,
    byte[]? Cover,
    string CoverLabel,
    string GameName);

public sealed record ScrapeOption(string Key, string Label)
{
    public override string ToString() => Label;
}

public sealed record SystemOption(int Id, string Name, SsSystem? System)
{
    public override string ToString() => Name;
}

/// <summary>One card field: the current value, the ScreenScraper value (editable) and whether to use it.</summary>
public sealed partial class ScrapeField(ScrapeFieldKind kind, string label, string current) : ObservableObject
{
    public ScrapeFieldKind Kind { get; } = kind;
    public string Label { get; } = label;
    public string Current { get; } = current;
    public string CurrentText => Current.Length > 0 ? Current : "(empty)";
    public bool IsMultiline => Kind == ScrapeFieldKind.Description;

    [ObservableProperty] public partial string Value { get; set; } = string.Empty;
    [ObservableProperty] public partial bool Use { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSame))]
    public partial bool HasValue { get; set; }

    public bool IsSame => string.Equals(Value.Trim(), Current.Trim(), StringComparison.Ordinal);

    partial void OnValueChanged(string value)
    {
        HasValue = value.Trim().Length > 0;
        OnPropertyChanged(nameof(IsSame));
    }
}

/// <summary>A search result: the game as ScreenScraper sent it in the list, completed on selection.</summary>
public sealed partial class ScrapeResult(SsGame game) : ObservableObject
{
    public SsGame Game { get; set; } = game;

    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string Detail { get; set; } = string.Empty;

    public void Update(string region)
    {
        Title = Game.Name(region) is { Length: > 0 } name ? name : $"Game #{Game.Id}";
        Detail = string.Join("  ·  ", new[] { Game.SystemName, Game.Year(region), Game.DeveloperOrPublisher }
            .Where(s => s.Length > 0));
    }
}

/// <summary>
/// The ScreenScraper window: finds the game by ROM (hashes) or by name, lets the user pick a result,
/// a language, a region and a picture, and tick which card fields to fill.
/// </summary>
public sealed partial class ScraperViewModel : ObservableObject, IDisposable
{
    private readonly ScreenScraperSettings _settings;
    private readonly ScreenScraperClient _client;
    private readonly ScrapeRequest _request;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _mediaCts;
    private byte[]? _previewBytes;
    private bool _initialised;
    private bool _ready;

    public ScraperViewModel(ScrapeRequest request)
    {
        _request = request;
        _settings = ScreenScraperSettings.Load();
        _client = new ScreenScraperClient(_settings);

        SearchName = request.Current.GetValueOrDefault(ScrapeFieldKind.Name, string.Empty);
        RomName = request.RomPath is null ? string.Empty : Path.GetFileName(request.RomPath);

        foreach (var (kind, label) in new[]
                 {
                     (ScrapeFieldKind.Name, "Name"), (ScrapeFieldKind.Platform, "Platform"), (ScrapeFieldKind.Year, "Year"),
                     (ScrapeFieldKind.Developer, "Developer"), (ScrapeFieldKind.Genre, "Genre"),
                     (ScrapeFieldKind.Players, "Players"), (ScrapeFieldKind.Description, "Description")
                 })
            Fields.Add(new ScrapeField(kind, label, request.Current.GetValueOrDefault(kind, string.Empty)));

        Systems.Add(AnySystem);
        SelectedSystem = AnySystem;
        SelectedLanguage = Languages.FirstOrDefault(l => l.Key == _settings.Language) ?? Languages[0];
        SelectedRegion = Regions.FirstOrDefault(r => r.Key == _settings.Region) ?? Regions[0];

        User = _settings.User;
        Password = _settings.Password;

        // A card without cover gets the picture ticked; one with a cover keeps it unless ticked.
        UseCover = !request.HasCover;
        _ready = true;
    }

    public event EventHandler<ScrapeSelection?>? CloseRequested;

    // ------------------------------------------------------------ search

    private static readonly SystemOption AnySystem = new(0, "Any system", null);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchByNameCommand))]
    public partial string SearchName { get; set; }

    public string RomName { get; }
    public bool HasRom => RomName.Length > 0;
    public string RomText => HasRom ? $"ROM: {RomName}" : "No ROM in the card yet: search by name.";

    public ObservableCollection<SystemOption> Systems { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchByRomCommand))]
    public partial SystemOption? SelectedSystem { get; set; }

    public IReadOnlyList<ScrapeOption> Languages { get; } =
    [
        new("en", "English"), new("fr", "Français"), new("de", "Deutsch"), new("es", "Español"), new("it", "Italiano"),
        new("pt", "Português"), new("nl", "Nederlands"), new("ja", "日本語"), new("ru", "Русский"), new("pl", "Polski"),
        new("sv", "Svenska"), new("da", "Dansk"), new("fi", "Suomi"), new("no", "Norsk"), new("cz", "Čeština"),
        new("hu", "Magyar"), new("tr", "Türkçe"), new("ko", "한국어"), new("zh", "中文")
    ];

    public IReadOnlyList<ScrapeOption> Regions { get; } =
    [
        new("wor", "World"), new("eu", "Europe"), new("us", "USA"), new("jp", "Japan"), new("fr", "France"),
        new("de", "Germany"), new("es", "Spain"), new("it", "Italy"), new("uk", "United Kingdom"), new("br", "Brazil"),
        new("au", "Australia"), new("kr", "Korea"), new("cn", "China"), new("ss", "ScreenScraper")
    ];

    [ObservableProperty] public partial ScrapeOption SelectedLanguage { get; set; }
    [ObservableProperty] public partial ScrapeOption SelectedRegion { get; set; }

    private string Language => SelectedLanguage.Key;
    private string Region => SelectedRegion.Key;

    partial void OnSelectedLanguageChanged(ScrapeOption value)
    {
        _settings.Language = value.Key;
        SaveSettingsQuietly();
        UpdateFields(keepUse: true);
    }

    partial void OnSelectedRegionChanged(ScrapeOption value)
    {
        _settings.Region = value.Key;
        SaveSettingsQuietly();
        foreach (var result in Results)
            result.Update(value.Key);
        UpdateFields(keepUse: true);
        UpdateMediaTypes();
    }

    public ObservableCollection<ScrapeResult> Results { get; } = [];

    [ObservableProperty] public partial bool NoResults { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(SearchByNameCommand), nameof(SearchByRomCommand), nameof(ApplyCommand))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty] public partial string Status { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StatusIsError { get; set; }
    [ObservableProperty] public partial string QuotaText { get; set; } = string.Empty;

    /// <summary>Loads the systems, picks the card's one, then looks the ROM up (or searches by name).</summary>
    public async Task InitializeAsync()
    {
        if (_initialised)
            return;
        _initialised = true;

        if (!ScreenScraperSettings.HasDevLogin)
        {
            SetStatus("This build has no ScreenScraper developer login: build it with SCREENSCRAPER_DEVID / SCREENSCRAPER_DEVPASSWORD set.", true);
            return;
        }

        if (!await LoadSystemsAsync())
            return;

        if (HasRom && SelectedSystem is { Id: > 0 } && await SearchRomAsync())
            return;

        if (SearchName.Trim().Length > 0)
            await SearchByNameAsync();
        else
            SetStatus(HasRom ? "Choose the system, then search by ROM, or type a name." : "Type a name to search.");
    }

    private async Task<bool> LoadSystemsAsync()
    {
        if (Systems.Count > 1)
            return true;

        IsBusy = true;
        SetStatus("Loading the ScreenScraper systems…");
        try
        {
            var systems = await _client.GetSystemsAsync(CancellationToken.None);
            foreach (var system in systems)
                Systems.Add(new SystemOption(system.Id, system.Name, system));
            SelectedSystem = MatchSystem(systems) is { } match
                ? Systems.First(s => s.Id == match.Id)
                : AnySystem;
            SetStatus(string.Empty);
            return true;
        }
        catch (ScreenScraperException exception)
        {
            SetStatus(exception.Message, true);
            return false;
        }
        finally
        {
            IsBusy = false;
            UpdateQuota();
        }
    }

    /// <summary>The card's system: its Recalbox system name, then its platform, then usual aliases.</summary>
    private SsSystem? MatchSystem(IReadOnlyList<SsSystem> systems)
    {
        static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        static IEnumerable<string> Split(string names) =>
            names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var recalbox = _request.RecalboxSystem.Trim();
        var platform = _request.Current.GetValueOrDefault(ScrapeFieldKind.Platform, string.Empty).Trim();

        if (recalbox.Length > 0 && systems.FirstOrDefault(s => Split(s.RecalboxName).Any(n => Same(n, recalbox))) is { } byRecalbox)
            return byRecalbox;
        if (platform.Length > 0 &&
            systems.FirstOrDefault(s => Same(s.Name, platform) || s.OtherNames.Any(n => Same(n, platform))) is { } byPlatform)
            return byPlatform;

        // Arcade emulators have no ScreenScraper system of their own: arcade games are under MAME.
        var alias = recalbox.ToLowerInvariant() switch
        {
            "fbneo" or "fba" or "fba_libretro" or "fbalpha" or "arcade" or "mame2003" or "mame2003_plus" or "mame2010" => "mame",
            _ => null
        };
        return alias is null ? null : systems.FirstOrDefault(s => Split(s.RecalboxName).Any(n => Same(n, alias)));
    }

    private bool CanSearchByName() => !IsBusy && SearchName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSearchByName))]
    private async Task SearchByNameAsync()
    {
        var cancellation = Restart(ref _searchCts);
        IsBusy = true;
        SetStatus($"Searching \"{SearchName.Trim()}\"…");
        try
        {
            if (!await LoadSystemsAsync())
                return;
            IsBusy = true;

            var systemId = SelectedSystem is { Id: > 0 } system ? system.Id : (int?)null;
            var games = await _client.SearchAsync(SearchName.Trim(), systemId, cancellation);
            ShowResults(games);
            SetStatus(games.Count switch
            {
                0 => systemId is null
                    ? "No game found with this name."
                    : "No game found with this name on this system. Try \"Any system\", or a shorter name.",
                1 => "1 game found.",
                _ => $"{games.Count} games found: choose the right one."
            });
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
        }
        catch (ScreenScraperException exception)
        {
            SetStatus(exception.Message, true);
        }
        finally
        {
            IsBusy = false;
            UpdateQuota();
        }
    }

    private bool CanSearchByRom() => !IsBusy && HasRom && SelectedSystem is { Id: > 0 };

    [RelayCommand(CanExecute = nameof(CanSearchByRom))]
    private async Task SearchByRomAsync() => await SearchRomAsync();

    /// <summary>Looks the card's first ROM up by its hashes. True when ScreenScraper knows it.</summary>
    private async Task<bool> SearchRomAsync()
    {
        if (_request.RomPath is not { } path || SelectedSystem is not { Id: > 0 } system)
            return false;

        var cancellation = Restart(ref _searchCts);
        IsBusy = true;
        try
        {
            SetStatus($"Reading {RomName}…");
            var hash = await RomHash.ComputeAsync(path, cancellation);
            SetStatus($"Looking {RomName} up on ScreenScraper ({system.Name})…");
            var game = await _client.FindByRomAsync(hash, system.Id, cancellation);
            if (game is null)
            {
                SetStatus($"ScreenScraper doesn't know {RomName} on {system.Name}. Search by name instead.");
                return false;
            }

            ShowResults([game]);
            SetStatus($"Found by ROM: {game.Name(Region)}");
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
            return false;
        }
        catch (ScreenScraperException exception)
        {
            SetStatus(exception.Message, true);
            return false;
        }
        catch (IOException exception)
        {
            SetStatus($"Could not read {RomName}: {exception.Message}", true);
            return false;
        }
        finally
        {
            IsBusy = false;
            UpdateQuota();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _searchCts?.Cancel();
        _detailCts?.Cancel();
        _mediaCts?.Cancel();
    }

    private void ShowResults(IReadOnlyList<SsGame> games)
    {
        Results.Clear();
        foreach (var game in games)
        {
            var result = new ScrapeResult(game);
            result.Update(Region);
            Results.Add(result);
        }

        NoResults = Results.Count == 0;
        SelectedResult = Results.FirstOrDefault();
    }

    // ------------------------------------------------------------ selected game

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGame))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial SsGame? Game { get; set; }

    public bool HasGame => Game is not null;

    [ObservableProperty] public partial ScrapeResult? SelectedResult { get; set; }

    [ObservableProperty] public partial bool IsLoadingGame { get; set; }

    public ObservableCollection<ScrapeField> Fields { get; } = [];

    partial void OnSelectedResultChanged(ScrapeResult? value) => _ = LoadGameAsync(value);

    /// <summary>The search list has partial games: the full one (all medias, synopsis) is fetched once.</summary>
    private async Task LoadGameAsync(ScrapeResult? result)
    {
        var cancellation = Restart(ref _detailCts);
        if (result is null)
        {
            Game = null;
            UpdateFields(keepUse: false);
            UpdateMediaTypes();
            return;
        }

        Game = result.Game;
        UpdateFields(keepUse: false);
        UpdateMediaTypes();

        IsLoadingGame = true;
        try
        {
            if (await _client.GetGameAsync(result.Game.Id, cancellation) is { } full && SelectedResult == result)
            {
                result.Game = full;
                result.Update(Region);
                Game = full;
                UpdateFields(keepUse: true);
                UpdateMediaTypes();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ScreenScraperException exception)
        {
            SetStatus(exception.Message, true);
        }
        finally
        {
            if (SelectedResult == result)
                IsLoadingGame = false;
            UpdateQuota();
        }
    }

    private void UpdateFields(bool keepUse)
    {
        foreach (var field in Fields)
        {
            field.Value = Game is null ? string.Empty : field.Kind switch
            {
                ScrapeFieldKind.Name => Game.Name(Region),
                ScrapeFieldKind.Description => Game.Description(Language),
                ScrapeFieldKind.Platform => Game.SystemName,
                ScrapeFieldKind.Year => Game.Year(Region),
                ScrapeFieldKind.Developer => Game.DeveloperOrPublisher,
                ScrapeFieldKind.Genre => Game.Genre(Language),
                ScrapeFieldKind.Players => Game.Players,
                _ => string.Empty
            };

            if (!field.HasValue)
                field.Use = false;
            else if (!keepUse)
                // Empty card fields are filled, the ones already written are kept unless ticked.
                field.Use = field.Current.Trim().Length == 0;
        }
    }

    // ------------------------------------------------------------ media

    public ObservableCollection<ScrapeOption> MediaTypes { get; } = [];

    [ObservableProperty] public partial ScrapeOption? SelectedMediaType { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial Bitmap? Preview { get; set; }

    public bool HasPreview => Preview is not null;

    [ObservableProperty] public partial string PreviewText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsLoadingPreview { get; set; }
    [ObservableProperty] public partial bool UseCover { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoMedia))]
    public partial bool HasMedia { get; set; }

    public bool NoMedia => HasGame && !HasMedia;

    partial void OnGameChanged(SsGame? value) => OnPropertyChanged(nameof(NoMedia));

    partial void OnSelectedMediaTypeChanged(ScrapeOption? value)
    {
        if (value is not null && _settings.MediaType != value.Key)
        {
            _settings.MediaType = value.Key;
            SaveSettingsQuietly();
        }

        _ = LoadPreviewAsync();
    }

    private void UpdateMediaTypes()
    {
        var previous = SelectedMediaType?.Key ?? _settings.MediaType;
        var types = Game?.ImageTypes() ?? [];

        // Same list (region change, full game loaded): only the picture may change.
        if (types.SequenceEqual(MediaTypes.Select(m => m.Key)))
        {
            _ = LoadPreviewAsync();
            return;
        }

        MediaTypes.Clear();
        foreach (var type in types)
            MediaTypes.Add(new ScrapeOption(type, SsGame.MediaLabel(type)));
        HasMedia = MediaTypes.Count > 0;

        var selected = MediaTypes.FirstOrDefault(m => m.Key == previous) ??
                       MediaTypes.FirstOrDefault(m => m.Key == _settings.MediaType) ??
                       MediaTypes.FirstOrDefault();
        if (selected == SelectedMediaType)
            _ = LoadPreviewAsync();
        else
            SelectedMediaType = selected;
    }

    private async Task LoadPreviewAsync()
    {
        var cancellation = Restart(ref _mediaCts);
        var media = Game is { } game && SelectedMediaType is { } type ? game.Media(type.Key, Region) : null;

        SetPreview(null, null);
        if (media is null)
        {
            PreviewText = string.Empty;
            return;
        }

        IsLoadingPreview = true;
        PreviewText = "Downloading…";
        try
        {
            var bytes = await _client.DownloadAsync(media.Url, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var bitmap = new Bitmap(new MemoryStream(bytes));
            SetPreview(bitmap, bytes);
            var region = Regions.FirstOrDefault(r => r.Key == media.Region)?.Label ?? media.Region;
            PreviewText = $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height}" +
                          (region.Length > 0 ? $"  ·  {region}" : string.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ScreenScraperException exception)
        {
            PreviewText = exception.Message;
        }
        catch (Exception exception)
        {
            Log.Error("ScreenScraper picture", exception);
            PreviewText = "This picture can't be shown.";
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
                IsLoadingPreview = false;
            UpdateQuota();
        }
    }

    private void SetPreview(Bitmap? bitmap, byte[]? bytes)
    {
        var old = Preview;
        Preview = bitmap;
        _previewBytes = bytes;
        if (old is not null && !ReferenceEquals(old, bitmap))
            old.Dispose();
    }

    // ------------------------------------------------------------ settings

    [ObservableProperty] public partial bool ShowSettings { get; set; }
    [ObservableProperty] public partial string User { get; set; }
    [ObservableProperty] public partial string Password { get; set; }
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        _settings.User = User.Trim();
        _settings.Password = Password;
        _settings.Save();
        SetStatus("Settings saved.");

        // The first try failed (quota, API closed to non-members...): try again with the account.
        if (!_initialised || Systems.Count <= 1)
        {
            _initialised = false;
            await InitializeAsync();
        }
    }

    private void SaveSettingsQuietly()
    {
        // Called from the constructor too (initial selection): nothing to save then.
        if (_ready)
            _settings.Save();
    }

    // ------------------------------------------------------------ apply

    private bool CanApply() => !IsBusy && Game is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (Game is null)
            return;

        var fields = Fields.Where(f => f.Use && f.HasValue).ToDictionary(f => f.Kind, f => f.Value.Trim());
        var cover = UseCover ? _previewBytes : null;
        var label = SelectedMediaType?.Label ?? string.Empty;
        if (fields.Count == 0 && cover is null)
        {
            SetStatus("Nothing is ticked: tick the fields (or the picture) to put in the card.", true);
            return;
        }

        CloseRequested?.Invoke(this, new ScrapeSelection(fields, cover, label, Game.Name(Region)));
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, null);

    // ------------------------------------------------------------ helpers

    private static CancellationToken Restart(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        return cts.Token;
    }

    private void UpdateQuota()
    {
        if (_client.Quota is { } quota)
            QuotaText = $"Requests today: {quota.RequestsToday:N0} / {quota.MaxRequestsPerDay:N0}";
    }

    private void SetStatus(string message, bool error = false)
    {
        Status = message;
        StatusIsError = error;
        if (error)
            ShowSettings = ShowSettings || message.Contains("account", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Cancel();
        Preview?.Dispose();
    }
}

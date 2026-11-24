using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGcCreator.Services.ScreenScraper;

public sealed class ScreenScraperException(string message) : Exception(message);

/// <summary>The account's quota, from the <c>ssuser</c> part of each answer.</summary>
public sealed record SsQuota(int RequestsToday, int MaxRequestsPerDay);

/// <summary>
/// ScreenScraper api2 client (https://api.screenscraper.fr/api2/). One request at a time (free accounts get
/// a single thread), answers parsed with <see cref="JsonNode"/> so it works in trimmed builds.
/// </summary>
public sealed class ScreenScraperClient(ScreenScraperSettings settings)
{
    private const string BaseUrl = "https://api.screenscraper.fr/api2/";
    private const string SoftName = "retrogc-creator";

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<int, SsGame> Games = new();
    private static readonly ConcurrentDictionary<string, byte[]> Downloads = new();
    private static IReadOnlyList<SsSystem>? _systems;

    private static string SystemsCache => Path.Combine(ScreenScraperSettings.Folder, "screenscraper-systems.json");

    /// <summary>The quota from the last answer (null until a request was made, or without a user account).</summary>
    public SsQuota? Quota { get; private set; }

    // ------------------------------------------------------------ games

    /// <summary>Games whose name matches (ScreenScraper returns up to 30), optionally for one system.</summary>
    public async Task<IReadOnlyList<SsGame>> SearchAsync(string name, int? systemId, CancellationToken cancellation)
    {
        var query = new List<(string, string)> { ("recherche", name) };
        if (systemId is > 0)
            query.Add(("systemeid", systemId.Value.ToString()));

        var response = await GetJsonAsync("jeuRecherche.php", query, cancellation);
        return response is null
            ? []
            : SsGame.Items(response["jeux"]).Select(SsGame.Parse).OfType<SsGame>().ToList();
    }

    /// <summary>The full game (every media, synopsis...), cached for the session.</summary>
    public async Task<SsGame?> GetGameAsync(int gameId, CancellationToken cancellation)
    {
        if (Games.TryGetValue(gameId, out var cached))
            return cached;

        var response = await GetJsonAsync("jeuInfos.php", [("gameid", gameId.ToString())], cancellation);
        var game = SsGame.Parse(response?["jeu"]);
        if (game is not null)
            Games[game.Id] = game;
        return game;
    }

    /// <summary>The game of a ROM (by its hashes, name and size), or null when ScreenScraper doesn't know it.</summary>
    public async Task<SsGame?> FindByRomAsync(RomHash rom, int systemId, CancellationToken cancellation)
    {
        var response = await GetJsonAsync("jeuInfos.php",
        [
            ("systemeid", systemId.ToString()),
            ("romtype", "rom"),
            ("crc", rom.Crc32),
            ("md5", rom.Md5),
            ("sha1", rom.Sha1),
            ("romnom", rom.FileName),
            ("romtaille", rom.Size.ToString())
        ], cancellation);
        var game = SsGame.Parse(response?["jeu"]);
        if (game is not null)
            Games[game.Id] = game;
        return game;
    }

    // ------------------------------------------------------------ systems

    /// <summary>Every ScreenScraper system, cached on disk for a week (it rarely changes).</summary>
    public async Task<IReadOnlyList<SsSystem>> GetSystemsAsync(CancellationToken cancellation)
    {
        if (_systems is not null)
            return _systems;

        JsonNode? cached = null;
        try
        {
            if (File.Exists(SystemsCache) && File.GetLastWriteTimeUtc(SystemsCache) > DateTime.UtcNow.AddDays(-7))
                cached = JsonNode.Parse(await File.ReadAllTextAsync(SystemsCache, cancellation));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error("ScreenScraper systems cache", exception);
        }

        var response = cached ?? await GetJsonAsync("systemesListe.php", [], cancellation);
        var systems = SsGame.Items(response?["systemes"]).Select(SsGame.ParseSystem).OfType<SsSystem>()
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

        if (cached is null && response is not null && systems.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(ScreenScraperSettings.Folder);
                await File.WriteAllTextAsync(SystemsCache,
                    new JsonObject { ["systemes"] = response["systemes"]?.DeepClone() }.ToJsonString(), cancellation);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Error("ScreenScraper systems cache", exception);
            }
        }

        _systems = systems;
        return systems;
    }

    // ------------------------------------------------------------ media

    /// <summary>Downloads a media (its URL already carries the logins), cached for the session.</summary>
    public async Task<byte[]> DownloadAsync(string url, CancellationToken cancellation)
    {
        if (Downloads.TryGetValue(url, out var cached))
            return cached;

        await Gate.WaitAsync(cancellation);
        try
        {
            using var response = await Http.GetAsync(url, cancellation);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
            if (!response.IsSuccessStatusCode)
                throw new ScreenScraperException(ErrorMessage(response.StatusCode, bytes));

            // An error comes back as a short text ("NOMEDIA"...), not a picture.
            if (bytes.Length < 64 && System.Text.Encoding.UTF8.GetString(bytes).Trim() is var text && text.All(c => c < 128))
                throw new ScreenScraperException($"ScreenScraper sent no picture ({text}).");

            Downloads[url] = bytes;
            return bytes;
        }
        catch (HttpRequestException exception)
        {
            Log.Error($"ScreenScraper download {StripQuery(url)}", exception);
            throw new ScreenScraperException($"Could not reach ScreenScraper: {exception.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    // ------------------------------------------------------------ requests

    /// <summary>
    /// Sends an api2 request, returns its <c>response</c> object, or null when ScreenScraper found nothing
    /// (HTTP 404). Other errors become a <see cref="ScreenScraperException"/> with a readable message.
    /// </summary>
    private async Task<JsonObject?> GetJsonAsync(string endpoint, IReadOnlyList<(string Key, string Value)> query,
        CancellationToken cancellation)
    {
        if (!ScreenScraperSettings.HasDevLogin)
            throw new ScreenScraperException(
                "This build has no ScreenScraper developer login (build it with SCREENSCRAPER_DEVID / SCREENSCRAPER_DEVPASSWORD set).");

        var parameters = new List<(string Key, string Value)>
        {
            ("devid", ScreenScraperDev.Id),
            ("devpassword", ScreenScraperDev.Password),
            ("softname", SoftName),
            ("output", "json")
        };
        if (settings.User.Length > 0)
        {
            parameters.Add(("ssid", settings.User));
            parameters.Add(("sspassword", settings.Password));
        }

        parameters.AddRange(query);
        var url = BaseUrl + endpoint + "?" +
                  string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        await Gate.WaitAsync(cancellation);
        try
        {
            using var response = await Http.GetAsync(url, cancellation);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new ScreenScraperException(ErrorMessage(response.StatusCode, bytes));

            JsonNode? json;
            try
            {
                json = JsonNode.Parse(bytes);
            }
            catch (JsonException)
            {
                // Some errors come back as plain text with HTTP 200.
                throw new ScreenScraperException($"ScreenScraper: {Text(bytes)}");
            }

            var answer = json?["response"] as JsonObject;
            if (answer is null)
                throw new ScreenScraperException("ScreenScraper sent an unexpected answer.");

            if (answer["ssuser"] is JsonObject user &&
                int.TryParse(SsGame.Str(user["requeststoday"]), out var today) &&
                int.TryParse(SsGame.Str(user["maxrequestsperday"]), out var max))
                Quota = new SsQuota(today, max);

            return answer;
        }
        catch (HttpRequestException exception)
        {
            Log.Error($"ScreenScraper {endpoint}", exception);
            throw new ScreenScraperException($"Could not reach ScreenScraper: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ScreenScraperException("ScreenScraper did not answer in time. Try again later.");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string ErrorMessage(HttpStatusCode status, byte[] body)
    {
        var message = (int)status switch
        {
            400 => "the request was refused (bad parameters).",
            401 => "the API is closed to non-members right now (server busy). Log in with a ScreenScraper account, or try later.",
            403 => "the login was refused. Check your ScreenScraper account in the settings below.",
            423 => "the API is closed right now. Try again later.",
            426 => "this software is refused by ScreenScraper (outdated or blocked).",
            429 => "too many requests at once. Wait a moment and try again.",
            430 => "your daily request quota is used up. Try again tomorrow, or log in with a ScreenScraper account.",
            431 => "too many not-found requests today. Try again tomorrow.",
            _ => $"error {(int)status}."
        };
        var text = Text(body);
        Log.Error($"ScreenScraper HTTP {(int)status}: {text}");
        return $"ScreenScraper: {message}";
    }

    private static string Text(byte[] body)
    {
        var text = System.Text.Encoding.UTF8.GetString(body).Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    private static string StripQuery(string url) => url.Split('?')[0];

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{SoftName}/{version}");
        return http;
    }
}

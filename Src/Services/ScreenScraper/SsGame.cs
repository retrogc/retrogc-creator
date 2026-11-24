using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetroGcCreator.Services.ScreenScraper;

/// <summary>A text for one region (names, dates) or one language (synopsis, genre names).</summary>
public sealed record SsText(string Key, string Text);

public sealed record SsGenre(bool IsMain, IReadOnlyList<SsText> Names);

/// <summary>A picture (or video, manual...) of a game. <see cref="Region"/> is empty when it has none.</summary>
public sealed record SsMedia(string Type, string Region, string Url, string Format);

public sealed record SsSystem(int Id, string Name, string RecalboxName, IReadOnlyList<string> OtherNames)
{
    public override string ToString() => Name;
}

/// <summary>
/// A ScreenScraper game (<c>jeu</c>), read from the api2 JSON with <see cref="JsonNode"/> (trim-safe).
/// </summary>
public sealed record SsGame(
    int Id,
    IReadOnlyList<SsText> Names,
    int SystemId,
    string SystemName,
    string Developer,
    string Publisher,
    string Players,
    IReadOnlyList<SsText> Synopsis,
    IReadOnlyList<SsText> Dates,
    IReadOnlyList<SsGenre> Genres,
    IReadOnlyList<SsMedia> Medias)
{
    /// <summary>Region fallbacks after the chosen one: world first, like ScreenScraper's own defaults.</summary>
    private static readonly string[] RegionFallbacks = ["wor", "us", "eu", "ss", "jp"];

    public static SsGame? Parse(JsonNode? node)
    {
        if (node is not JsonObject game || !int.TryParse(Str(game["id"]), out var id) || id <= 0)
            return null;

        var system = game["systeme"];
        return new SsGame(
            id,
            Texts(game["noms"], "region"),
            int.TryParse(Str(system?["id"]), out var systemId) ? systemId : 0,
            Html(Str(system?["text"])),
            Html(Str(game["developpeur"]?["text"])),
            Html(Str(game["editeur"]?["text"])),
            Html(Str(game["joueurs"]?["text"])),
            Texts(game["synopsis"], "langue"),
            Texts(game["dates"], "region"),
            Items(game["genres"]).Select(g => new SsGenre(Str(g["principale"]) == "1", Texts(g["noms"], "langue"))).ToList(),
            Items(game["medias"])
                .Select(m => new SsMedia(Str(m["type"]), Str(m["region"]), Str(m["url"]), Str(m["format"]).ToLowerInvariant()))
                .Where(m => m.Type.Length > 0 && m.Url.Length > 0)
                .ToList());
    }

    public static SsSystem? ParseSystem(JsonNode? node)
    {
        if (node is not JsonObject system || !int.TryParse(Str(system["id"]), out var id))
            return null;

        var names = system["noms"];
        var name = Str(names?["nom_eu"]);
        if (name.Length == 0) name = Str(names?["nom_us"]);
        var others = new List<string>();
        foreach (var key in new[] { "nom_eu", "nom_us", "nom_jp", "nom_retropie", "nom_launchbox", "nom_hyperspin" })
            if (Str(names?[key]) is { Length: > 0 } other)
                others.Add(other);
        others.AddRange(Str(names?["noms_commun"]).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return name.Length == 0 ? null : new SsSystem(id, name, Str(names?["nom_recalbox"]), others);
    }

    // ------------------------------------------------------------ picking

    public string Name(string region) => PickRegion(Names, region)?.Text ?? string.Empty;

    public string Description(string language) => PickLanguage(Synopsis, language)?.Text.Trim() ?? string.Empty;

    /// <summary>The year of the release date in the chosen region (dates are "1991" or "1991-06-23").</summary>
    public string Year(string region)
    {
        var date = PickRegion(Dates, region)?.Text ?? string.Empty;
        return date.Length >= 4 ? date[..4] : date;
    }

    public string DeveloperOrPublisher => Developer.Length > 0 ? Developer : Publisher;

    public string Genre(string language)
    {
        var genre = Genres.FirstOrDefault(g => g.IsMain) ?? Genres.FirstOrDefault();
        return genre is null ? string.Empty : PickLanguage(genre.Names, language)?.Text ?? string.Empty;
    }

    /// <summary>Picture media types this game has (videos, manuals... left out), in a stable order.</summary>
    public IReadOnlyList<string> ImageTypes() => Medias
        .Where(m => m.Format is "png" or "jpg" or "jpeg" or "gif" or "webp")
        .Select(m => m.Type)
        .Distinct()
        .OrderBy(t => MediaOrder.IndexOf(t) is var i && i >= 0 ? i : int.MaxValue)
        .ThenBy(t => t, StringComparer.Ordinal)
        .ToList();

    /// <summary>The media of a type for the chosen region (world, then other regions, then any).</summary>
    public SsMedia? Media(string type, string region)
    {
        var candidates = Medias.Where(m => m.Type == type).ToList();
        if (candidates.Count == 0)
            return null;

        foreach (var r in new[] { region }.Concat(RegionFallbacks))
            if (candidates.FirstOrDefault(m => m.Region == r) is { } match)
                return match;
        return candidates.FirstOrDefault(m => m.Region.Length == 0) ?? candidates[0];
    }

    public static SsText? PickRegion(IReadOnlyList<SsText> texts, string region)
    {
        foreach (var r in new[] { region }.Concat(RegionFallbacks))
            if (texts.FirstOrDefault(t => t.Key == r && t.Text.Length > 0) is { } match)
                return match;
        return texts.FirstOrDefault(t => t.Text.Length > 0);
    }

    public static SsText? PickLanguage(IReadOnlyList<SsText> texts, string language) =>
        texts.FirstOrDefault(t => t.Key == language && t.Text.Length > 0) ??
        texts.FirstOrDefault(t => t.Key == "en" && t.Text.Length > 0) ??
        texts.FirstOrDefault(t => t.Text.Length > 0);

    // ------------------------------------------------------------ media labels

    private static readonly List<string> MediaOrder =
    [
        "box-2D", "box-3D", "mixrbv2", "mixrbv1", "ss", "sstitle", "fanart", "wheel", "wheel-hd", "wheel-carbon",
        "wheel-steel", "screenmarquee", "screenmarqueesmall", "marquee", "support-2D", "box-2D-back", "box-2D-side"
    ];

    public static string MediaLabel(string type) => type switch
    {
        "box-2D" => "Box (2D)",
        "box-3D" => "Box (3D)",
        "box-2D-back" => "Box (back)",
        "box-2D-side" => "Box (side)",
        "box-texture" => "Box (texture)",
        "ss" => "Screenshot",
        "sstitle" => "Title screen",
        "fanart" => "Fan art",
        "wheel" => "Logo",
        "wheel-hd" => "Logo (HD)",
        "wheel-carbon" => "Logo (carbon)",
        "wheel-steel" => "Logo (steel)",
        "mixrbv1" => "Mix (v1)",
        "mixrbv2" => "Mix (v2)",
        "screenmarquee" => "Marquee",
        "screenmarqueesmall" => "Marquee (small)",
        "marquee" => "Arcade marquee",
        "support-2D" => "Cartridge / disc",
        "support-texture" => "Cartridge / disc (texture)",
        "steamgrid" => "Steam grid",
        _ => type
    };

    // ------------------------------------------------------------ json helpers

    /// <summary>A JSON value as text: ScreenScraper sends numbers as strings, or not.</summary>
    public static string Str(JsonNode? node) => node is JsonValue value
        ? value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
            _ => string.Empty
        }
        : string.Empty;

    /// <summary>ScreenScraper texts carry HTML entities (&amp;quot; &amp;amp;...): decoded to plain text.</summary>
    private static string Html(string text) => System.Net.WebUtility.HtmlDecode(text).Trim();

    public static IEnumerable<JsonObject> Items(JsonNode? node) => node is JsonArray array
        ? array.OfType<JsonObject>()
        : node is JsonObject single ? [single] : [];

    private static List<SsText> Texts(JsonNode? node, string key) =>
        Items(node).Select(t => new SsText(Str(t[key]), Html(Str(t["text"])))).Where(t => t.Text.Length > 0).ToList();
}

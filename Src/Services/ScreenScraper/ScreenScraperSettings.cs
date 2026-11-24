using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetroGcCreator.Services.ScreenScraper;

/// <summary>
/// ScreenScraper preferences and logins, in the user's settings folder
/// (<c>%APPDATA%\retrogc-creator</c> on Windows, <c>~/.config/retrogc-creator</c> on Linux).
/// Passwords are stored as plain text there, like most scrapers do.
/// </summary>
public sealed class ScreenScraperSettings
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "retrogc-creator");

    private static string FilePath => Path.Combine(Folder, "screenscraper.json");

    /// <summary>Optional ScreenScraper user account: more requests per day, more threads.</summary>
    public string User { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public string Language { get; set; } = "en";
    public string Region { get; set; } = "wor";
    public string MediaType { get; set; } = "box-2D";

    /// <summary>The developer login built into the app (see <see cref="ScreenScraperDev"/>).</summary>
    public static bool HasDevLogin => ScreenScraperDev.Id.Length > 0 && ScreenScraperDev.Password.Length > 0;

    public static ScreenScraperSettings Load()
    {
        var settings = new ScreenScraperSettings();
        try
        {
            if (!File.Exists(FilePath) || JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject json)
                return settings;

            settings.User = SsGame.Str(json["user"]);
            settings.Password = SsGame.Str(json["password"]);
            if (SsGame.Str(json["language"]) is { Length: > 0 } language) settings.Language = language;
            if (SsGame.Str(json["region"]) is { Length: > 0 } region) settings.Region = region;
            if (SsGame.Str(json["media"]) is { Length: > 0 } media) settings.MediaType = media;
        }
        catch (Exception exception)
        {
            Log.Error("ScreenScraper settings", exception);
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var json = new JsonObject
            {
                ["user"] = User,
                ["password"] = Password,
                ["language"] = Language,
                ["region"] = Region,
                ["media"] = MediaType
            };
            File.WriteAllText(FilePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            Log.Error("ScreenScraper settings", exception);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace RetroGcCreator.Services.ScreenScraper;

/// <summary>
/// The Recalbox system (its ROM folder name) of a ScreenScraper system. ScreenScraper gives it
/// (<c>nom_recalbox</c>) for most systems; this fixes the cases where it doesn't fit a card:
/// <list type="bullet">
/// <item>arcade systems (MAME and every board under it: CPS-1, Neo-Geo, Cave...) follow the card's RetroArch
/// core, <c>fbneo</c> or <c>mame</c>, like the cards do; with another core (flycast...) the board's own
/// system is used (<c>naomi</c>, <c>atomiswave</c>...);</item>
/// <item>a few systems have no name, a list of names, or a value that isn't one.</item>
/// </list>
/// </summary>
public static class RecalboxSystems
{
    /// <summary>ScreenScraper's MAME system: the parent of every arcade system, and the home of games without a board.</summary>
    public const int MameSystemId = 75;

    /// <summary>Generic arcade entries of ScreenScraper's lists: emulators, not the system of one game.</summary>
    private static readonly HashSet<string> GenericArcade = new(StringComparer.OrdinalIgnoreCase)
        { "arcade", "mame", "fba", "fba_libretro", "fbneo", "dice" };

    /// <summary>ScreenScraper system id → Recalbox system, where ScreenScraper's value doesn't fit.</summary>
    private static readonly Dictionary<int, string> Overrides = new()
    {
        [117] = "msx2", // MSX2+ ("msx,msx2": Recalbox has no "msx")
    };

    public static string For(SsSystem? system, string retroArchCore)
    {
        if (system is null)
            return string.Empty;

        var names = system.RecalboxName
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsSystemName)
            .ToList();

        if (system.Id == MameSystemId || system.ParentId == MameSystemId)
            return Arcade(names, retroArchCore);

        if (Overrides.TryGetValue(system.Id, out var name))
            return name;

        return names.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>Arcade games go in the folder of the emulator that runs them.</summary>
    private static string Arcade(IReadOnlyList<string> names, string core)
    {
        core = core.Trim().ToLowerInvariant();
        if (core.StartsWith("fbneo", StringComparison.Ordinal) || core.StartsWith("fba", StringComparison.Ordinal))
            return "fbneo";
        if (core.StartsWith("mame", StringComparison.Ordinal))
            return "mame";

        // Another core (flycast for Naomi / Atomiswave...): the board's own Recalbox system.
        return names.FirstOrDefault(n => !GenericArcade.Contains(n)) ?? "fbneo";
    }

    /// <summary>Recalbox system names are lower-case folder names ("megadrive", "game.com"…), not labels.</summary>
    private static bool IsSystemName(string name) =>
        name.Length > 1 && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
}

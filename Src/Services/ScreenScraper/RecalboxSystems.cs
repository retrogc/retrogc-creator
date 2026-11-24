using System;
using System.Collections.Generic;
using System.Linq;

namespace RetroGcCreator.Services.ScreenScraper;

/// <summary>
/// The Recalbox system (its ROM folder name) of a ScreenScraper system. ScreenScraper gives it
/// (<c>nom_recalbox</c>) for most systems; this fixes the cases where it is missing, generic or not a name:
/// <list type="bullet">
/// <item>arcade boards all say <c>arcade,mame,fba,fba_libretro</c>: on Recalbox the system is the emulator's,
/// <c>fbneo</c> or <c>mame</c>, chosen from the card's RetroArch core;</item>
/// <item>a list with a specific entry (<c>…,atomiswave</c>) gives that entry;</item>
/// <item>a few systems have no name, or a value that isn't one.</item>
/// </list>
/// </summary>
public static class RecalboxSystems
{
    /// <summary>Generic arcade entries of ScreenScraper's lists: not a Recalbox system for one game.</summary>
    private static readonly HashSet<string> GenericArcade = new(StringComparer.OrdinalIgnoreCase)
        { "arcade", "mame", "fba", "fba_libretro", "fbneo" };

    /// <summary>ScreenScraper system id → Recalbox system, where ScreenScraper's value is missing.</summary>
    private static readonly Dictionary<int, string> Overrides = new()
    {
        [68] = "neogeo", // Neo-Geo MVS
        [117] = "msx2",  // MSX2+ ("msx,msx2": Recalbox has no "msx")
        [75] = "",       // MAME: decided by the core, like the other arcade boards
    };

    public static string For(SsSystem? system, string retroArchCore)
    {
        if (system is null)
            return string.Empty;

        if (Overrides.TryGetValue(system.Id, out var name) && name.Length > 0)
            return name;

        var names = system.RecalboxName
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsSystemName)
            .ToList();

        var specific = names.FirstOrDefault(n => !GenericArcade.Contains(n));
        if (specific is not null)
            return specific;

        return names.Count > 0 || system.Id == 75 ? ArcadeSystem(retroArchCore) : string.Empty;
    }

    /// <summary>Arcade games go in the folder of the emulator that runs them.</summary>
    private static string ArcadeSystem(string core)
    {
        core = core.Trim().ToLowerInvariant();
        return core.StartsWith("mame", StringComparison.Ordinal) ? "mame" : "fbneo";
    }

    /// <summary>Recalbox system names are lower-case folder names ("megadrive", "game.com"…), not labels.</summary>
    private static bool IsSystemName(string name) =>
        name.Length > 1 && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
}

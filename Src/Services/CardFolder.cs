using System;
using System.IO;
using System.Linq;

namespace RetroGcCreator.Services;

public enum PathState
{
    Empty,
    Present,
    Missing,
    Invalid
}

/// <summary>
/// Card folder helpers: finding the definition, resolving and checking the relative paths of a card
/// (same rules as the launcher: relative, inside the card), and importing files into it.
/// </summary>
public static class CardFolder
{
    public const string InstallerManifest = "retrogc-data.rgc";

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The first <c>.rgc</c> at the root of <paramref name="folder"/> (installer manifests excluded).</summary>
    public static string? FindDefinition(string folder) =>
        Directory.EnumerateFiles(folder, "*.rgc", SearchOption.TopDirectoryOnly)
            .Where(f => !string.Equals(Path.GetFileName(f), InstallerManifest, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>Absolute path of a card relative path, or null when it is absolute or escapes the card.</summary>
    public static string? Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            return null;

        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSeparator, PathComparison) ? full : null;
    }

    public static PathState Check(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return PathState.Empty;

        var full = Resolve(root, relative.TrimStart('/'));
        if (full is null)
            return PathState.Invalid;

        // FAT32 can't hold these names: the image build would fail.
        if (relative.Split('/', '\\').Any(part => part.Length > 0 && !Fat32ImageWriter.IsValidLongName(part)))
            return PathState.Invalid;

        return File.Exists(full) ? PathState.Present : PathState.Missing;
    }

    /// <summary>Relative path (with '/') of <paramref name="file"/> when it is inside the card folder.</summary>
    public static string? RelativeTo(string root, string file)
    {
        var full = Path.GetFullPath(file);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSeparator, PathComparison)
            ? Path.GetRelativePath(root, full).Replace('\\', '/')
            : null;
    }

    /// <summary>
    /// Puts <paramref name="source"/> into the card: kept in place when already inside the card,
    /// otherwise copied to <c>&lt;folder&gt;/&lt;file name&gt;</c>. Returns the card relative path.
    /// </summary>
    public static string Import(string root, string source, string folder)
    {
        if (RelativeTo(root, source) is { } inside)
            return inside;

        var relative = string.IsNullOrEmpty(folder)
            ? Path.GetFileName(source)
            : $"{folder.Trim('/')}/{Path.GetFileName(source)}";
        var destination = Resolve(root, relative)
                          ?? throw new InvalidOperationException($"Invalid destination '{relative}'.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        Log.Info($"Imported '{source}' -> '{destination}'");
        return relative;
    }

    /// <summary>Puts <paramref name="source"/> exactly at <paramref name="relative"/> inside the card.</summary>
    public static void ImportAs(string root, string source, string relative)
    {
        var destination = Resolve(root, relative.TrimStart('/'))
                          ?? throw new InvalidOperationException($"Invalid path '{relative}'.");
        if (string.Equals(Path.GetFullPath(source), destination, PathComparison))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        Log.Info($"Imported '{source}' -> '{destination}'");
    }
}

using System;
using System.Collections.Generic;
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

    /// <summary>A file or folder of the card folder that the definition doesn't use.</summary>
    /// <param name="Display">Card relative path, with a trailing '/' for a folder.</param>
    public sealed record Unreferenced(string Display, string FullPath, bool IsDirectory);

    /// <summary>
    /// Everything under <paramref name="root"/> that is neither <paramref name="definition"/> nor one of the
    /// <paramref name="referenced"/> card paths. A folder holding nothing referenced (or empty) is reported
    /// once, as a folder, instead of file by file.
    /// </summary>
    public static IReadOnlyList<Unreferenced> FindUnreferenced(string root, string definition, IEnumerable<string> referenced)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var used = new HashSet<string>(comparer) { Path.GetFullPath(definition) };
        foreach (var relative in referenced.Concat(AutorunFiles(root)))
        {
            if (Resolve(root, relative.Trim().TrimStart('/')) is { } full)
                used.Add(full);
        }

        var result = new List<Unreferenced>();
        Walk(new DirectoryInfo(Path.GetFullPath(root)), result);
        return result;

        // Returns true when nothing under the folder is used (the folder itself can go).
        bool Walk(DirectoryInfo directory, List<Unreferenced> items)
        {
            var wholly = true;
            foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entry is DirectoryInfo sub && sub.LinkTarget is null)
                {
                    var inside = new List<Unreferenced>();
                    if (Walk(sub, inside))
                        items.Add(new Unreferenced(Display(sub.FullName) + "/", sub.FullName, true));
                    else
                    {
                        wholly = false;
                        items.AddRange(inside);
                    }
                }
                else if (used.Contains(entry.FullName))
                {
                    wholly = false;
                }
                else
                {
                    items.Add(new Unreferenced(Display(entry.FullName), entry.FullName, entry is DirectoryInfo));
                }
            }

            return wholly;
        }

        string Display(string full) => Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    /// <summary>
    /// <c>autorun.inf</c> at the root (drive icon and label on Windows) and the icon it names: part of
    /// the card layout, though not in the definition.
    /// </summary>
    private static IEnumerable<string> AutorunFiles(string root)
    {
        var autorun = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "autorun.inf", StringComparison.OrdinalIgnoreCase));
        if (autorun is null)
            yield break;

        yield return Path.GetFileName(autorun);

        string[] lines;
        try
        {
            lines = File.ReadAllLines(autorun);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var line in lines)
        {
            // icon=autorun.ico  /  icon=file.ico,0
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("icon", StringComparison.OrdinalIgnoreCase) || trimmed.IndexOf('=') is not (> 0 and var equals))
                continue;
            if (!string.Equals(trimmed[..equals].Trim(), "icon", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = trimmed[(equals + 1)..].Split(',')[0].Trim().Trim('"').Replace('\\', '/');
            if (value.Length > 0)
                yield return value;
        }
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

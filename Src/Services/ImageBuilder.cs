using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Joveler.Compression.XZ;

namespace RetroGcCreator.Services;

public enum BuildStage
{
    Writing,
    Compressing
}

/// <summary>Image build progress: stage and its fraction (0..1).</summary>
public readonly record struct BuildProgress(BuildStage Stage, double Fraction);

/// <summary>
/// Builds a compressed card image (<c>.img.xz</c>) from a card folder: FAT32 labelled RETROGC,
/// identical in layout to <c>retrogc-image.sh --build</c>.
/// </summary>
public static class ImageBuilder
{
    public const string Label = "RETROGC";

    private static readonly Lock XzLock = new();
    private static bool _xzReady;

    public static async Task BuildAsync(
        string cardFolder, string outputPath, IProgress<BuildProgress>? progress = null, CancellationToken ct = default)
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(outputDirectory);

        // Raw image next to the output (same volume, often big), removed at the end.
        var rawPath = Path.Combine(outputDirectory, $".{Path.GetFileName(outputPath)}.{Environment.ProcessId}.img");
        var partialPath = outputPath + ".part";

        try
        {
            Log.Info($"Writing image: {cardFolder} -> {rawPath}");
            await Fat32ImageWriter.WriteAsync(cardFolder, rawPath, Label,
                new Progress<double>(f => progress?.Report(new BuildProgress(BuildStage.Writing, f))), ct);

            Log.Info($"Compressing: {outputPath}");
            await Task.Run(() => Compress(rawPath, partialPath, progress, ct), ct);
            File.Move(partialPath, outputPath, overwrite: true);
            Log.Info($"Image ready: {outputPath}");
        }
        finally
        {
            TryDelete(rawPath);
            TryDelete(partialPath);
        }
    }

    // ------------------------------------------------------------ xz

    private static void Compress(string rawPath, string outputPath, IProgress<BuildProgress>? progress, CancellationToken ct)
    {
        EnsureXz();

        const LzmaCompLevel level = LzmaCompLevel.Level9;
        var threads = Math.Max(1, Environment.ProcessorCount);

        // xz -9 needs a lot of memory per thread: stay under half of the physical memory.
        var budget = XZHardware.PhysMem() / 2;
        var compressOptions = new XZCompressOptions { Level = level, LeaveOpen = true };
        while (threads > 1 &&
               XZMemory.ParallelEncoderMemUsage(compressOptions, new XZParallelCompressOptions { Threads = threads }) > budget)
            threads--;

        using var input = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.SequentialScan);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using (var xz = new XZStream(output, compressOptions, new XZParallelCompressOptions { Threads = threads }))
        {
            var buffer = new byte[4 << 20];
            long done = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                xz.Write(buffer, 0, read);
                done += read;
                progress?.Report(new BuildProgress(BuildStage.Compressing, (double)done / input.Length));
            }
        }

        output.Flush(true);
    }

    /// <summary>Loads the native liblzma shipped by Joveler.Compression.XZ.</summary>
    internal static void EnsureXz()
    {
        lock (XzLock)
        {
            if (_xzReady)
                return;

            var library = OperatingSystem.IsWindows() ? "liblzma.dll"
                : OperatingSystem.IsMacOS() ? "liblzma.dylib"
                : "liblzma.so";
            var rid = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" +
                      RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

            // Published apps have it next to the executable, builds under runtimes/<rid>/native.
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, library),
                Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", library)
            ];

            var path = Array.Find(candidates, File.Exists)
                       ?? throw new FileNotFoundException($"Native xz library not found ({library}).");
            XZInit.GlobalInit(path);
            _xzReady = true;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception)
        {
            Log.Error($"Could not delete '{path}'", exception);
        }
    }
}

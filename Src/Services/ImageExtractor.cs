using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGcCreator.Services;

public enum ExtractStage
{
    Decompressing,
    Extracting
}

public readonly record struct ExtractProgress(ExtractStage Stage, double Fraction);

/// <summary>Unpacks a card image (<c>.img.xz</c> or raw <c>.img</c>) into a folder.</summary>
public static class ImageExtractor
{
    /// <summary>Extracts every file of the image into <paramref name="destination"/>. Returns the volume label.</summary>
    public static Task<string> ExtractAsync(
        string image, string destination, IProgress<ExtractProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            Directory.CreateDirectory(destination);
            string? rawPath = null;
            try
            {
                var source = image;
                if (Xz.IsXz(image))
                {
                    // Next to the destination: same volume, and it may be big.
                    rawPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!,
                        $".retrogc-extract-{Environment.ProcessId}-{Guid.NewGuid():N}.img");
                    Log.Info($"Decompressing {image}");
                    using (var raw = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                        Xz.DecompressTo(image, raw,
                            new Progress<double>(f => progress?.Report(new ExtractProgress(ExtractStage.Decompressing, f))), ct);
                    source = rawPath;
                }

                using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                var reader = new FatImageReader(stream);
                Log.Info($"Extracting {reader.FatType} volume '{reader.Label}' to {destination}");
                if (!string.Equals(reader.Label, ImageBuilder.Label, StringComparison.OrdinalIgnoreCase))
                    Log.Info($"Note: the volume label is '{reader.Label}', not {ImageBuilder.Label}");

                reader.ExtractAll(destination,
                    new Progress<double>(f => progress?.Report(new ExtractProgress(ExtractStage.Extracting, f))), ct);
                return reader.Label;
            }
            finally
            {
                if (rawPath is not null)
                {
                    try
                    {
                        File.Delete(rawPath);
                    }
                    catch (IOException exception)
                    {
                        Log.Error($"Could not delete '{rawPath}'", exception);
                    }
                }
            }
        }, ct);
}

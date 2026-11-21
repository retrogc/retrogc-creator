using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Joveler.Compression.XZ;

namespace RetroGcCreator.Services;

/// <summary>xz decompression shared by the image extractor and the USB writer.</summary>
public static class Xz
{
    public static bool IsXz(string path) => path.EndsWith(".xz", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decompresses <paramref name="image"/> into <paramref name="output"/>; progress is the compressed fraction read.</summary>
    public static void DecompressTo(string image, Stream output, IProgress<double>? progress, CancellationToken ct,
        int bufferSize = 4 << 20)
    {
        ImageBuilder.EnsureXz();

        using var input = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var xz = new XZStream(input, new XZDecompressOptions { LeaveOpen = true });
        var buffer = new byte[bufferSize];
        var lastReport = Stopwatch.StartNew();
        int read;
        while ((read = xz.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            if (lastReport.ElapsedMilliseconds >= 200)
            {
                lastReport.Restart();
                progress?.Report((double)input.Position / input.Length);
            }
        }

        output.Flush();
        progress?.Report(1);
    }
}

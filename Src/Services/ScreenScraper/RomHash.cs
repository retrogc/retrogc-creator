using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGcCreator.Services.ScreenScraper;

/// <summary>What ScreenScraper needs to identify a ROM: name, size, CRC32, MD5 and SHA1 (upper-case hex).</summary>
public sealed record RomHash(string FileName, long Size, string Crc32, string Md5, string Sha1)
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Reads the file once and computes the three hashes.</summary>
    public static Task<RomHash> ComputeAsync(string path, CancellationToken cancellation = default) => Task.Run(() =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var crc = 0xFFFFFFFFu;
        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var span = buffer.AsSpan(0, read);
            md5.AppendData(span);
            sha1.AppendData(span);
            foreach (var b in span)
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return new RomHash(Path.GetFileName(path), stream.Length, (~crc).ToString("X8"),
            Convert.ToHexString(md5.GetHashAndReset()), Convert.ToHexString(sha1.GetHashAndReset()));
    }, cancellation);

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        return table;
    }
}

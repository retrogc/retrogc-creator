using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RetroGcCreator.Services;

/// <summary>
/// Reads the files of a FAT12/16/32 volume in a raw disk image (MBR partition or whole-disk volume).
/// Counterpart of <see cref="Fat32ImageWriter"/>, also reads images made by mkfs.fat / mtools.
/// </summary>
public sealed class FatImageReader
{
    private const byte AttrVolumeId = 0x08, AttrDirectory = 0x10, AttrLongName = 0x0F;
    private static readonly byte[] FatPartitionTypes = [0x01, 0x04, 0x06, 0x0B, 0x0C, 0x0E];

    private readonly Stream _image;
    private readonly long _volumeOffset;
    private readonly int _bytesPerSector;
    private readonly int _sectorsPerCluster;
    private readonly long _fatOffset;
    private readonly long _rootDirOffset;   // FAT12/16 fixed root directory
    private readonly int _rootDirBytes;
    private readonly long _dataOffset;
    private readonly uint _rootCluster;     // FAT32
    private readonly long _clusterCount;
    private readonly int _fatBits;
    private readonly byte[] _fat;

    public string Label { get; }
    public string FatType => $"FAT{_fatBits}";

    private int ClusterBytes => _bytesPerSector * _sectorsPerCluster;

    public sealed record Entry(string Name, bool IsDirectory, uint FirstCluster, uint Size, DateTime LastWriteTime);

    public FatImageReader(Stream image)
    {
        _image = image;
        var sector0 = ReadAt(0, 512);

        _volumeOffset = FindVolume(sector0);
        var boot = _volumeOffset == 0 ? sector0 : ReadAt(_volumeOffset, 512);

        if (Encoding.ASCII.GetString(boot, 3, 8) == "EXFAT   ")
            throw new InvalidDataException(
                "This image is exFAT (an installer card?). Only FAT game card images can be opened.");

        _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
        _sectorsPerCluster = boot[13];
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14));
        int fatCount = boot[16];
        int rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17));
        long totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19));
        if (totalSectors == 0)
            totalSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32));
        long fatSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22));
        if (fatSectors == 0)
            fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36));

        if (_bytesPerSector is not (512 or 1024 or 2048 or 4096) || _sectorsPerCluster == 0 ||
            (_sectorsPerCluster & (_sectorsPerCluster - 1)) != 0 || fatCount == 0 || fatSectors == 0 || reserved == 0)
            throw new InvalidDataException("This image doesn't contain a FAT volume.");

        _rootDirBytes = rootEntries * 32;
        var rootDirSectors = (_rootDirBytes + _bytesPerSector - 1) / _bytesPerSector;
        _fatOffset = _volumeOffset + (long)reserved * _bytesPerSector;
        _rootDirOffset = _fatOffset + fatCount * fatSectors * _bytesPerSector;
        _dataOffset = _rootDirOffset + (long)rootDirSectors * _bytesPerSector;

        var dataSectors = totalSectors - reserved - fatCount * fatSectors - rootDirSectors;
        _clusterCount = dataSectors / _sectorsPerCluster;
        _fatBits = _clusterCount < 4085 ? 12 : _clusterCount < 65525 ? 16 : 32;
        _rootCluster = _fatBits == 32 ? BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(44)) : 0;

        // First FAT copy, in memory (a few MB at most for card sized volumes).
        var fatBytes = Math.Min(fatSectors * _bytesPerSector, (_clusterCount + 2) * 4 + _bytesPerSector);
        _fat = ReadAt(_fatOffset, (int)fatBytes);

        var bootLabel = Encoding.ASCII.GetString(boot, _fatBits == 32 ? 71 : 43, 11).TrimEnd();
        Label = FindRootLabel() ?? (bootLabel == "NO NAME" ? string.Empty : bootLabel);
    }

    // ------------------------------------------------------------ volume

    private static long FindVolume(byte[] sector0)
    {
        if (sector0[510] != 0x55 || sector0[511] != 0xAA)
            throw new InvalidDataException("This file isn't a disk image (no boot signature).");

        // A whole-disk volume starts with a jump instruction and a sane BPB.
        var isBootSector = (sector0[0] == 0xEB || sector0[0] == 0xE9) &&
                           BinaryPrimitives.ReadUInt16LittleEndian(sector0.AsSpan(11)) is 512 or 1024 or 2048 or 4096 &&
                           sector0[13] != 0 && sector0[16] != 0;
        var exfat = Encoding.ASCII.GetString(sector0, 3, 8) == "EXFAT   ";
        if (isBootSector || exfat)
            return 0;

        for (var i = 0; i < 4; i++)
        {
            var entry = sector0.AsSpan(446 + i * 16, 16);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (count == 0 || start == 0)
                continue;

            if (Array.IndexOf(FatPartitionTypes, entry[4]) >= 0 || entry[4] == 0x07)
                return start * 512L;
        }

        throw new InvalidDataException("No FAT partition found in this image.");
    }

    // ------------------------------------------------------------ directories

    public IReadOnlyList<Entry> ReadRoot() =>
        _fatBits == 32 ? ParseDirectory(ReadChain(_rootCluster)) : ParseDirectory(ReadAt(_rootDirOffset, _rootDirBytes));

    public IReadOnlyList<Entry> ReadDirectory(Entry directory) => ParseDirectory(ReadChain(directory.FirstCluster));

    private string? FindRootLabel()
    {
        var root = _fatBits == 32 ? ReadChain(_rootCluster) : ReadAt(_rootDirOffset, _rootDirBytes);
        for (var offset = 0; offset + 32 <= root.Length; offset += 32)
        {
            if (root[offset] == 0x00)
                break;
            if (root[offset] == 0xE5 || root[offset + 11] == AttrLongName)
                continue;
            if ((root[offset + 11] & AttrVolumeId) != 0)
                return Encoding.ASCII.GetString(root, offset, 11).TrimEnd();
        }

        return null;
    }

    private static List<Entry> ParseDirectory(byte[] data)
    {
        var entries = new List<Entry>();
        var longName = new char[0];
        var longChecksum = -1;

        for (var offset = 0; offset + 32 <= data.Length; offset += 32)
        {
            var e = data.AsSpan(offset, 32);
            if (e[0] == 0x00)
                break;
            if (e[0] == 0xE5)
            {
                longChecksum = -1;
                continue;
            }

            if (e[11] == AttrLongName)
            {
                var ordinal = e[0] & 0x3F;
                if ((e[0] & 0x40) != 0)
                {
                    longName = new char[ordinal * 13];
                    longChecksum = e[13];
                }

                if (ordinal == 0 || ordinal * 13 > longName.Length || e[13] != longChecksum)
                {
                    longChecksum = -1;
                    continue;
                }

                int[] offsets = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
                for (var i = 0; i < 13; i++)
                    longName[(ordinal - 1) * 13 + i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(e[offsets[i]..]);
                continue;
            }

            var attributes = e[11];
            if ((attributes & AttrVolumeId) != 0)
            {
                longChecksum = -1;
                continue;
            }

            var shortName = e[..11].ToArray();
            string name;
            if (longChecksum >= 0 && longChecksum == Checksum(shortName))
            {
                var end = Array.IndexOf(longName, '\0');
                name = new string(longName, 0, end >= 0 ? end : longName.Length).TrimEnd('￿');
            }
            else
            {
                name = ShortName(shortName, e[12]);
            }

            longChecksum = -1;
            if (name is "." or "..")
                continue;

            var cluster = ((uint)BinaryPrimitives.ReadUInt16LittleEndian(e[20..]) << 16) |
                          BinaryPrimitives.ReadUInt16LittleEndian(e[26..]);
            entries.Add(new Entry(name, (attributes & AttrDirectory) != 0, cluster,
                BinaryPrimitives.ReadUInt32LittleEndian(e[28..]),
                DosToDateTime(BinaryPrimitives.ReadUInt16LittleEndian(e[24..]), BinaryPrimitives.ReadUInt16LittleEndian(e[22..]))));
        }

        return entries;
    }

    private static string ShortName(byte[] raw, byte caseFlags)
    {
        if (raw[0] == 0x05)
            raw[0] = 0xE5;

        var basePart = Encoding.Latin1.GetString(raw, 0, 8).TrimEnd();
        var extension = Encoding.Latin1.GetString(raw, 8, 3).TrimEnd();
        // Windows NT case flags: 0x08 lower case name, 0x10 lower case extension.
        if ((caseFlags & 0x08) != 0) basePart = basePart.ToLowerInvariant();
        if ((caseFlags & 0x10) != 0) extension = extension.ToLowerInvariant();
        return extension.Length > 0 ? $"{basePart}.{extension}" : basePart;
    }

    private static byte Checksum(byte[] shortName)
    {
        byte sum = 0;
        foreach (var b in shortName)
            sum = (byte)(((sum & 1) << 7) + (sum >> 1) + b);
        return sum;
    }

    private static DateTime DosToDateTime(ushort date, ushort time)
    {
        try
        {
            return new DateTime(1980 + (date >> 9), Math.Max(1, (date >> 5) & 0xF), Math.Max(1, date & 0x1F),
                time >> 11, (time >> 5) & 0x3F, Math.Min(59, (time & 0x1F) * 2), DateTimeKind.Local);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new DateTime(1980, 1, 1);
        }
    }

    // ------------------------------------------------------------ clusters

    private uint NextCluster(uint cluster) => _fatBits switch
    {
        32 => BinaryPrimitives.ReadUInt32LittleEndian(_fat.AsSpan((int)(cluster * 4))) & 0x0FFFFFFF,
        16 => BinaryPrimitives.ReadUInt16LittleEndian(_fat.AsSpan((int)(cluster * 2))),
        _ => (cluster & 1) == 0
            ? (uint)(BinaryPrimitives.ReadUInt16LittleEndian(_fat.AsSpan((int)(cluster * 3 / 2))) & 0x0FFF)
            : (uint)(BinaryPrimitives.ReadUInt16LittleEndian(_fat.AsSpan((int)(cluster * 3 / 2))) >> 4)
    };

    private bool IsEndOrInvalid(uint cluster) => cluster < 2 || cluster >= _clusterCount + 2;

    /// <summary>Cluster chain from <paramref name="first"/>; stops on loops and corrupt links.</summary>
    private IEnumerable<uint> Chain(uint first)
    {
        var seen = new HashSet<uint>();
        for (var cluster = first; !IsEndOrInvalid(cluster); cluster = NextCluster(cluster))
        {
            if (!seen.Add(cluster))
                throw new InvalidDataException("Corrupt image: a cluster chain loops.");
            yield return cluster;
        }
    }

    private long ClusterOffset(uint cluster) => _dataOffset + (cluster - 2L) * ClusterBytes;

    private byte[] ReadChain(uint first)
    {
        using var memory = new MemoryStream();
        foreach (var cluster in Chain(first))
            memory.Write(ReadAt(ClusterOffset(cluster), ClusterBytes));
        return memory.ToArray();
    }

    /// <summary>Copies a file's data to <paramref name="output"/>.</summary>
    public void CopyFile(Entry file, Stream output, Action<long>? written = null, CancellationToken ct = default)
    {
        long remaining = file.Size;
        if (remaining == 0)
            return;

        var buffer = new byte[ClusterBytes];
        foreach (var cluster in Chain(file.FirstCluster))
        {
            ct.ThrowIfCancellationRequested();
            var take = (int)Math.Min(remaining, ClusterBytes);
            _image.Position = ClusterOffset(cluster);
            _image.ReadExactly(buffer, 0, take);
            output.Write(buffer, 0, take);
            remaining -= take;
            written?.Invoke(take);
            if (remaining == 0)
                return;
        }

        throw new InvalidDataException($"Corrupt image: '{file.Name}' is shorter than its size.");
    }

    /// <summary>Writes every directory and file of the volume under <paramref name="destination"/>.</summary>
    public void ExtractAll(string destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(destination);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        // Total size first, for the progress.
        var files = new List<(Entry Entry, string Path)>();
        var directories = new List<(Entry Entry, string Path)>();
        Collect(ReadRoot(), root, 0);

        long total = Math.Max(1, files.Sum(f => (long)f.Entry.Size)), done = 0;
        foreach (var (entry, path) in files)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                CopyFile(entry, output, n => progress?.Report((double)(done += n) / total), ct);
            File.SetLastWriteTime(path, entry.LastWriteTime);
        }

        foreach (var (entry, path) in directories)
            Directory.SetLastWriteTime(path, entry.LastWriteTime);
        progress?.Report(1);

        void Collect(IReadOnlyList<Entry> entries, string folder, int depth)
        {
            if (depth > 64)
                throw new InvalidDataException("Corrupt image: directories nested too deep.");

            foreach (var entry in entries)
            {
                if (entry.Name.Length == 0 || entry.Name.IndexOfAny(['/', '\\', '\0']) >= 0)
                    continue;

                var path = Path.GetFullPath(Path.Combine(folder, entry.Name));
                if (!path.StartsWith(rootWithSeparator, StringComparison.Ordinal))
                    throw new InvalidDataException($"Unsafe path in the image: {entry.Name}");

                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(path);
                    directories.Add((entry, path));
                    Collect(ReadDirectory(entry), path, depth + 1);
                }
                else
                {
                    files.Add((entry, path));
                }
            }
        }
    }

    private byte[] ReadAt(long offset, int count)
    {
        var buffer = new byte[count];
        _image.Position = offset;
        var read = 0;
        while (read < count)
        {
            var n = _image.Read(buffer, read, count - read);
            if (n == 0)
                throw new InvalidDataException("The image is truncated.");
            read += n;
        }

        return buffer;
    }
}

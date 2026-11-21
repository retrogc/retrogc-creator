using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGcCreator.Services;

/// <summary>
/// Writes a raw disk image: an MBR partition table with one FAT32 partition (type 0x0C, starting at
/// 1 MiB) holding a copy of a folder. Same layout as <c>retrogc-image.sh --build</c>
/// (parted msdos + mkfs.fat -F 32 + mcopy), without any external tool, so it runs on Windows too.
/// </summary>
/// <remarks>
/// Files are written contiguously, long file names are always kept (VFAT LFN entries), and the volume
/// label is stored both in the boot sector and in the root directory: Windows and udev read the latter.
/// </remarks>
public static class Fat32ImageWriter
{
    public const int SectorSize = 512;
    public const long PartitionStartSector = 2048; // 1 MiB

    private const int ReservedSectors = 32;
    private const int FatCount = 2;
    private const int FsInfoSector = 1;
    private const int BackupBootSector = 6;
    private const uint EndOfChain = 0x0FFFFFFF;
    private const int MinFat32Clusters = 65525;

    private const byte AttrReadOnly = 0x01, AttrVolumeId = 0x08, AttrDirectory = 0x10, AttrArchive = 0x20;
    private const byte AttrLongName = 0x0F;

    private sealed class Node
    {
        public required string Name { get; init; }
        public required bool IsDirectory { get; init; }
        public string? SourcePath { get; init; }
        public long Length { get; init; }
        public DateTime LastWriteTime { get; init; }
        public bool ReadOnly { get; init; }
        public List<Node> Children { get; } = [];
        public Node? Parent { get; init; }

        public byte[] ShortName { get; set; } = [];
        public bool NeedsLongName { get; set; }
        public uint FirstCluster { get; set; }
        public uint ClusterCount { get; set; }
    }

    private sealed record Layout(
        long TotalSectors,
        long PartitionSectors,
        int SectorsPerCluster,
        long FatSectors,
        long ClusterCount)
    {
        public int ClusterBytes => SectorsPerCluster * SectorSize;
        public long DataStartSector => PartitionStartSector + ReservedSectors + FatCount * FatSectors;
        public long ClusterOffset(uint cluster) => (DataStartSector + (cluster - 2L) * SectorsPerCluster) * SectorSize;
    }

    /// <summary>Total size of the files under <paramref name="folder"/> (what <c>du -sb</c> counts).</summary>
    public static long GetContentSize(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    /// <summary>
    /// Image size used by <c>retrogc-image.sh</c>: content + 16 MiB, at least 64 MiB, rounded up to 8 MiB.
    /// </summary>
    public static long GetDefaultImageSize(long contentBytes)
    {
        const long mib = 1024 * 1024;
        var sizeMb = (contentBytes + 16 * mib + mib - 1) / mib;
        sizeMb = Math.Max(sizeMb, 64);
        sizeMb = (sizeMb + 7) / 8 * 8;
        return sizeMb * mib;
    }

    /// <summary>
    /// Writes <paramref name="imagePath"/> from <paramref name="sourceFolder"/>. The image grows by 8 MiB
    /// steps when the default size can't hold the files with their cluster slack.
    /// Progress is the fraction of file bytes written.
    /// </summary>
    public static async Task<long> WriteAsync(
        string sourceFolder, string imagePath, string label,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var root = ScanFolder(sourceFolder);
        AssignShortNames(root);

        var imageSize = GetDefaultImageSize(GetContentSize(sourceFolder));
        Layout layout;
        while (true)
        {
            layout = ComputeLayout(imageSize);
            if (CountClusters(root, layout.ClusterBytes) <= layout.ClusterCount)
                break;
            imageSize += 8 * 1024 * 1024;
        }

        var fat = new uint[layout.ClusterCount + 2];
        fat[0] = 0x0FFFFFF8;
        fat[1] = EndOfChain;
        var nextCluster = 2u;
        Allocate(root, layout, fat, ref nextCluster);

        var totalBytes = Math.Max(1, CollectFiles(root).Sum(f => f.Length));
        long writtenBytes = 0;
        var volumeId = (uint)DateTime.UtcNow.Ticks ^ (uint)Environment.TickCount;

        await using var image = new FileStream(imagePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            1 << 20, FileOptions.Asynchronous);
        image.SetLength(layout.TotalSectors * SectorSize);

        await WriteAtAsync(image, 0, BuildMbr(layout), ct);

        var boot = BuildBootSector(layout, label, volumeId);
        var fsInfo = BuildFsInfo(layout.ClusterCount - (nextCluster - 2), nextCluster);
        var partitionOffset = PartitionStartSector * SectorSize;
        await WriteAtAsync(image, partitionOffset, boot, ct);
        await WriteAtAsync(image, partitionOffset + FsInfoSector * SectorSize, fsInfo, ct);
        await WriteAtAsync(image, partitionOffset + BackupBootSector * SectorSize, boot, ct);
        await WriteAtAsync(image, partitionOffset + (BackupBootSector + FsInfoSector) * SectorSize, fsInfo, ct);

        var fatBytes = new byte[layout.FatSectors * SectorSize];
        for (var i = 0; i < fat.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(fatBytes.AsSpan(i * 4), fat[i]);
        for (var copy = 0; copy < FatCount; copy++)
            await WriteAtAsync(image, (PartitionStartSector + ReservedSectors + copy * layout.FatSectors) * SectorSize, fatBytes, ct);

        var buffer = new byte[1 << 20];
        foreach (var node in Walk(root))
        {
            ct.ThrowIfCancellationRequested();

            if (node.IsDirectory)
            {
                var entries = BuildDirectory(node, label);
                var padded = new byte[node.ClusterCount * layout.ClusterBytes];
                entries.CopyTo(padded, 0);
                await WriteAtAsync(image, layout.ClusterOffset(node.FirstCluster), padded, ct);
                continue;
            }

            if (node.Length == 0)
                continue;

            image.Position = layout.ClusterOffset(node.FirstCluster);
            await using var source = new FileStream(node.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await image.WriteAsync(buffer.AsMemory(0, read), ct);
                writtenBytes += read;
                progress?.Report((double)writtenBytes / totalBytes);
            }
        }

        await image.FlushAsync(ct);
        progress?.Report(1);
        return image.Length;
    }

    // ------------------------------------------------------------ tree

    private static Node ScanFolder(string folder)
    {
        var info = new DirectoryInfo(folder);
        if (!info.Exists)
            throw new DirectoryNotFoundException($"Card folder not found: {folder}");

        var root = new Node { Name = string.Empty, IsDirectory = true, LastWriteTime = info.LastWriteTime };
        Fill(root, info);
        return root;

        static void Fill(Node parent, DirectoryInfo directory)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (entry is DirectoryInfo sub)
                {
                    // Never follow links out of the card.
                    if (sub.LinkTarget is not null)
                        continue;

                    var child = new Node
                    {
                        Name = sub.Name, IsDirectory = true, Parent = parent, LastWriteTime = sub.LastWriteTime
                    };
                    parent.Children.Add(child);
                    Fill(child, sub);
                }
                else if (entry is FileInfo file)
                {
                    parent.Children.Add(new Node
                    {
                        Name = file.Name,
                        IsDirectory = false,
                        Parent = parent,
                        SourcePath = file.FullName,
                        Length = file.Length,
                        LastWriteTime = file.LastWriteTime,
                        ReadOnly = file.IsReadOnly
                    });
                }
            }
        }
    }

    private static IEnumerable<Node> Walk(Node node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var descendant in Walk(child))
            yield return descendant;
    }

    private static IEnumerable<Node> CollectFiles(Node root) => Walk(root).Where(n => !n.IsDirectory);

    private static int DirectoryEntryCount(Node directory)
    {
        // root: volume label; sub directories: "." and ".."
        var count = directory.Parent is null ? 1 : 2;
        foreach (var child in directory.Children)
            count += 1 + (child.NeedsLongName ? LongNameEntryCount(child.Name) : 0);
        return count;
    }

    private static long CountClusters(Node root, int clusterBytes)
    {
        long clusters = 0;
        foreach (var node in Walk(root))
        {
            var bytes = node.IsDirectory ? DirectoryEntryCount(node) * 32L : node.Length;
            clusters += node.IsDirectory ? Math.Max(1, Ceil(bytes, clusterBytes)) : Ceil(bytes, clusterBytes);
        }

        return clusters;
    }

    private static void Allocate(Node root, Layout layout, uint[] fat, ref uint nextCluster)
    {
        foreach (var node in Walk(root))
        {
            var bytes = node.IsDirectory ? DirectoryEntryCount(node) * 32L : node.Length;
            var count = node.IsDirectory ? Math.Max(1, Ceil(bytes, layout.ClusterBytes)) : Ceil(bytes, layout.ClusterBytes);
            if (count == 0)
                continue;

            node.FirstCluster = nextCluster;
            node.ClusterCount = (uint)count;
            for (var i = 0; i < count; i++)
            {
                var cluster = nextCluster + (uint)i;
                fat[cluster] = i == count - 1 ? EndOfChain : cluster + 1;
            }

            nextCluster += (uint)count;
        }
    }

    // ------------------------------------------------------------ geometry

    private static Layout ComputeLayout(long imageSize)
    {
        var totalSectors = imageSize / SectorSize;
        var partitionSectors = totalSectors - PartitionStartSector;
        var partitionBytes = partitionSectors * SectorSize;

        // Microsoft's FAT32 cluster size table (keeps the cluster count valid for FAT32).
        var sectorsPerCluster = partitionBytes switch
        {
            <= 260L * 1024 * 1024 => 1,
            <= 8L * 1024 * 1024 * 1024 => 8,
            <= 16L * 1024 * 1024 * 1024 => 16,
            <= 32L * 1024 * 1024 * 1024 => 32,
            _ => 64
        };

        long fatSectors = 1, clusters;
        while (true)
        {
            clusters = (partitionSectors - ReservedSectors - FatCount * fatSectors) / sectorsPerCluster;
            var needed = Ceil((clusters + 2) * 4, SectorSize);
            if (needed <= fatSectors)
                break;
            fatSectors = needed;
        }

        if (clusters < MinFat32Clusters)
            throw new InvalidOperationException($"Image too small for FAT32 ({clusters} clusters).");

        return new Layout(totalSectors, partitionSectors, sectorsPerCluster, fatSectors, clusters);
    }

    // ------------------------------------------------------------ sectors

    private static byte[] BuildMbr(Layout layout)
    {
        var mbr = new byte[SectorSize];
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(440), (uint)Random.Shared.Next()); // disk signature

        var entry = mbr.AsSpan(446, 16);
        entry[0] = 0x00; // not bootable
        WriteChs(entry[1..4], PartitionStartSector);
        entry[4] = 0x0C; // FAT32 (LBA)
        WriteChs(entry[5..8], PartitionStartSector + layout.PartitionSectors - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)PartitionStartSector);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)layout.PartitionSectors);

        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        return mbr;
    }

    private static void WriteChs(Span<byte> target, long lba)
    {
        const int heads = 255, sectorsPerTrack = 63;
        var cylinder = lba / (heads * sectorsPerTrack);
        var head = lba / sectorsPerTrack % heads;
        var sector = lba % sectorsPerTrack + 1;
        if (cylinder > 1023)
            (cylinder, head, sector) = (1023, 254, 63);

        target[0] = (byte)head;
        target[1] = (byte)(sector | ((cylinder >> 2) & 0xC0));
        target[2] = (byte)(cylinder & 0xFF);
    }

    private static byte[] BuildBootSector(Layout layout, string label, uint volumeId)
    {
        var bs = new byte[SectorSize];
        var s = bs.AsSpan();

        s[0] = 0xEB; s[1] = 0x58; s[2] = 0x90;
        Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(s[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(s[11..], SectorSize);
        s[13] = (byte)layout.SectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], ReservedSectors);
        s[16] = FatCount;
        // root entry count (17) and 16-bit total sectors (19) stay 0 on FAT32
        s[21] = 0xF8; // fixed disk
        BinaryPrimitives.WriteUInt16LittleEndian(s[24..], 63);  // sectors per track
        BinaryPrimitives.WriteUInt16LittleEndian(s[26..], 255); // heads
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)PartitionStartSector); // hidden sectors
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], (uint)layout.PartitionSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], (uint)layout.FatSectors);
        // ext flags (40) = 0: FAT mirroring; version (42) = 0.0
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 2); // root cluster
        BinaryPrimitives.WriteUInt16LittleEndian(s[48..], FsInfoSector);
        BinaryPrimitives.WriteUInt16LittleEndian(s[50..], BackupBootSector);
        s[64] = 0x80; // drive number
        s[66] = 0x29; // extended boot signature
        BinaryPrimitives.WriteUInt32LittleEndian(s[67..], volumeId);
        LabelBytes(label).CopyTo(s[71..]);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(s[82..]);
        s[510] = 0x55;
        s[511] = 0xAA;
        return bs;
    }

    private static byte[] BuildFsInfo(long freeClusters, uint nextFree)
    {
        var info = new byte[SectorSize];
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0), 0x41615252);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(484), 0x61417272);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(488), (uint)freeClusters);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(492), nextFree);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(508), 0xAA550000);
        return info;
    }

    // ------------------------------------------------------------ directories

    private static byte[] BuildDirectory(Node directory, string label)
    {
        var entries = new List<byte[]>();
        var now = directory.LastWriteTime;

        if (directory.Parent is null)
        {
            entries.Add(ShortEntry(LabelBytes(label), AttrVolumeId, 0, 0, now));
        }
        else
        {
            entries.Add(ShortEntry(DotName("."), AttrDirectory, directory.FirstCluster, 0, now));
            // ".." points to cluster 0 when the parent is the root directory.
            var parentCluster = directory.Parent.Parent is null ? 0 : directory.Parent.FirstCluster;
            entries.Add(ShortEntry(DotName(".."), AttrDirectory, parentCluster, 0, now));
        }

        foreach (var child in directory.Children)
        {
            if (child.NeedsLongName)
                entries.AddRange(LongNameEntries(child.Name, child.ShortName));

            var attributes = child.IsDirectory ? AttrDirectory : (byte)(AttrArchive | (child.ReadOnly ? AttrReadOnly : 0));
            entries.Add(ShortEntry(child.ShortName, attributes, child.FirstCluster,
                child.IsDirectory ? 0 : (uint)child.Length, child.LastWriteTime));
        }

        return entries.SelectMany(e => e).ToArray();
    }

    private static byte[] ShortEntry(byte[] name, byte attributes, uint cluster, uint size, DateTime time)
    {
        var e = new byte[32];
        var s = e.AsSpan();
        name.CopyTo(s);
        s[11] = attributes;
        var (date, dosTime) = DosDateTime(time);
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], dosTime); // creation
        BinaryPrimitives.WriteUInt16LittleEndian(s[16..], date);
        BinaryPrimitives.WriteUInt16LittleEndian(s[18..], date);    // last access
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], (ushort)(cluster >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], dosTime); // last write
        BinaryPrimitives.WriteUInt16LittleEndian(s[24..], date);
        BinaryPrimitives.WriteUInt16LittleEndian(s[26..], (ushort)(cluster & 0xFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], size);
        return e;
    }

    private static (ushort Date, ushort Time) DosDateTime(DateTime time)
    {
        if (time.Year < 1980) time = new DateTime(1980, 1, 1);
        if (time.Year > 2107) time = new DateTime(2107, 12, 31, 23, 59, 58);
        var date = (ushort)(((time.Year - 1980) << 9) | (time.Month << 5) | time.Day);
        var dosTime = (ushort)((time.Hour << 11) | (time.Minute << 5) | (time.Second / 2));
        return (date, dosTime);
    }

    private static int LongNameEntryCount(string name) => (name.Length + 12) / 13;

    private static IEnumerable<byte[]> LongNameEntries(string name, byte[] shortName)
    {
        var checksum = ShortNameChecksum(shortName);
        var count = LongNameEntryCount(name);

        // Stored last part first.
        for (var ordinal = count; ordinal >= 1; ordinal--)
        {
            var e = new byte[32];
            e[0] = (byte)(ordinal == count ? ordinal | 0x40 : ordinal);
            e[11] = AttrLongName;
            e[13] = checksum;

            var offsets = new[] { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 };
            for (var i = 0; i < 13; i++)
            {
                var index = (ordinal - 1) * 13 + i;
                ushort c = index < name.Length ? name[index] : index == name.Length ? (ushort)0 : (ushort)0xFFFF;
                BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(offsets[i]), c);
            }

            yield return e;
        }
    }

    private static byte ShortNameChecksum(byte[] shortName)
    {
        byte sum = 0;
        foreach (var b in shortName)
            sum = (byte)(((sum & 1) << 7) + (sum >> 1) + b);
        return sum;
    }

    // ------------------------------------------------------------ 8.3 names

    private const string ShortNameSpecials = "!#$%&'()-@^_`{}~";

    private static void AssignShortNames(Node root)
    {
        foreach (var directory in Walk(root).Where(n => n.IsDirectory))
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in directory.Children)
            {
                if (child.Name.Length > 255)
                    throw new InvalidOperationException($"File name too long for FAT32: {child.Name}");
                if (!IsValidLongName(child.Name))
                    throw new InvalidOperationException($"File name not allowed on FAT32: '{child.Name}'");

                var exact = ExactShortName(child.Name);
                if (exact is not null && used.Add(exact))
                {
                    child.ShortName = Encoding.ASCII.GetBytes(exact);
                    child.NeedsLongName = false;
                    continue;
                }

                var (basePart, extension) = LossyShortName(child.Name);
                for (var n = 1; ; n++)
                {
                    var tail = "~" + n;
                    var candidate = basePart[..Math.Min(basePart.Length, 8 - tail.Length)] + tail;
                    var padded = candidate.PadRight(8) + extension.PadRight(3);
                    if (used.Add(padded))
                    {
                        child.ShortName = Encoding.ASCII.GetBytes(padded);
                        break;
                    }
                }

                child.NeedsLongName = true;
            }
        }
    }

    /// <summary>Characters FAT long names can't hold, plus the trailing space / dot rule.</summary>
    public static bool IsValidLongName(string name) =>
        name.Length > 0 && !name.EndsWith(' ') && !name.EndsWith('.') &&
        name.All(c => c >= 0x20 && "\\/:*?\"<>|".IndexOf(c) < 0);

    /// <summary>The 11 byte short name when <paramref name="name"/> is already a valid upper case 8.3 name.</summary>
    private static string? ExactShortName(string name)
    {
        if (name is "." or "..")
            return null;

        var dot = name.LastIndexOf('.');
        var basePart = dot < 0 ? name : name[..dot];
        var extension = dot < 0 ? string.Empty : name[(dot + 1)..];

        if (basePart.Length is 0 or > 8 || extension.Length > 3 || (dot >= 0 && extension.Length == 0))
            return null;
        if (!(basePart + extension).All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' || ShortNameSpecials.Contains(c)))
            return null;

        return basePart.PadRight(8) + extension.PadRight(3);
    }

    private static (string Base, string Extension) LossyShortName(string name)
    {
        static string Clean(string part) => new(part.ToUpperInvariant()
            .Where(c => c != ' ' && c != '.')
            .Select(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' || ShortNameSpecials.Contains(c) ? c : '_')
            .ToArray());

        var trimmed = name.TrimStart('.');
        var dot = trimmed.LastIndexOf('.');
        var basePart = Clean(dot < 0 ? trimmed : trimmed[..dot]);
        var extension = dot < 0 ? string.Empty : Clean(trimmed[(dot + 1)..]);

        if (basePart.Length == 0)
            basePart = "_";
        return (basePart[..Math.Min(basePart.Length, 6)], extension[..Math.Min(extension.Length, 3)]);
    }

    private static byte[] DotName(string name) => Encoding.ASCII.GetBytes(name.PadRight(11));

    private static byte[] LabelBytes(string label)
    {
        var clean = new string(label.ToUpperInvariant().Where(c => c is >= ' ' and <= '~' && c != '.').Take(11).ToArray());
        return Encoding.ASCII.GetBytes(clean.PadRight(11));
    }

    // ------------------------------------------------------------ helpers

    private static long Ceil(long value, long unit) => (value + unit - 1) / unit;

    private static async Task WriteAtAsync(FileStream stream, long offset, byte[] data, CancellationToken ct)
    {
        stream.Position = offset;
        await stream.WriteAsync(data, ct);
    }
}

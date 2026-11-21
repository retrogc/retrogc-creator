using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Joveler.Compression.XZ;
using Microsoft.Win32.SafeHandles;

namespace RetroGcCreator.Services;

/// <summary>
/// Writes a card image (<c>.img.xz</c>) to a whole USB disk.
/// Linux: decompressed in-process and piped to <c>pkexec dd</c> (polkit password prompt).
/// Windows: the app restarts itself elevated (UAC) in a headless helper mode that writes
/// <c>\\.\PhysicalDriveN</c> and reports progress through a small file.
/// </summary>
public static class Flasher
{
    public const string HelperArgument = "--flash-helper";

    private const int ChunkSize = 4 << 20; // multiple of every sector size

    /// <summary>Progress is the fraction of the compressed image consumed.</summary>
    public static Task FlashAsync(string image, UsbDrive drive, IProgress<double> progress, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
            return FlashLinuxAsync(image, drive, progress, ct);
        if (OperatingSystem.IsWindows())
            return FlashWindowsAsync(image, drive, progress, ct);
        throw new PlatformNotSupportedException("Writing to USB is supported on Linux and Windows.");
    }

    // ------------------------------------------------------------ Linux

    private static async Task FlashLinuxAsync(string image, UsbDrive drive, IProgress<double> progress, CancellationToken ct)
    {
        // Unmounting the user's own removable partitions needs no root (udisks).
        foreach (var partition in await UsbDrives.GetMountedPartitionsAsync(drive.Device, ct))
        {
            Log.Info($"Unmounting {partition}");
            await UsbDrives.RunAsync("udisksctl", ["unmount", "-b", partition], ct);
        }

        var info = new ProcessStartInfo("pkexec")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            ArgumentList = { "dd", $"of={drive.Device}", "bs=4M", "iflag=fullblock", "oflag=direct", "conv=fsync", "status=none" }
        };

        Log.Info($"Writing {image} to {drive.Device} (pkexec dd)");
        using var dd = Process.Start(info) ?? throw new InvalidOperationException("Could not start pkexec.");
        var errors = dd.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await using (var stdin = dd.StandardInput.BaseStream)
            {
                await Task.Run(() => Decompress(image, stdin, progress, ct), ct);
            }

            await dd.WaitForExitAsync(ct);
        }
        catch (Exception) when (!dd.HasExited)
        {
            // Closing stdin ends dd; it runs as root, so it can't be killed from here.
            await dd.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        catch (IOException) when (dd.HasExited)
        {
            // dd stopped early (password prompt cancelled, write error): reported below.
        }

        // 126/127: the polkit authentication was dismissed or failed.
        var message = (await errors).Trim();
        if (dd.ExitCode is 126 or 127)
            throw new UnauthorizedAccessException("Authentication was cancelled.");
        if (dd.ExitCode != 0)
            throw new InvalidOperationException($"Writing failed (dd exit code {dd.ExitCode}): {message}");
    }

    // ------------------------------------------------------------ Windows (GUI side)

    private static async Task FlashWindowsAsync(string image, UsbDrive drive, IProgress<double> progress, CancellationToken ct)
    {
        var progressFile = Path.Combine(Path.GetTempPath(), $"retrogc-flash-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable."))
        {
            UseShellExecute = true,
            Verb = "runas", // UAC prompt
            ArgumentList = { HelperArgument, image, drive.Device, progressFile }
        };

        Process? helper;
        try
        {
            helper = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new UnauthorizedAccessException("Administrator rights were refused.");
        }

        if (helper is null)
            throw new InvalidOperationException("Could not start the writer.");

        using (helper)
        {
            try
            {
                while (!helper.HasExited)
                {
                    ct.ThrowIfCancellationRequested();
                    ReportHelperProgress(progressFile, progress);
                    await Task.Delay(250, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Ask the helper to stop.
                TryWrite(progressFile + ".cancel", "cancel");
                await helper.WaitForExitAsync(CancellationToken.None);
                throw;
            }

            ReportHelperProgress(progressFile, progress);
            var status = TryRead(progressFile);
            TryDelete(progressFile);
            TryDelete(progressFile + ".cancel");

            if (helper.ExitCode != 0)
            {
                var error = status?.StartsWith("error ", StringComparison.Ordinal) == true ? status[6..] : $"exit code {helper.ExitCode}";
                throw new InvalidOperationException($"Writing failed: {error}");
            }
        }
    }

    private static void ReportHelperProgress(string file, IProgress<double> progress)
    {
        if (double.TryParse(TryRead(file), NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction))
            progress.Report(fraction);
    }

    // ------------------------------------------------------------ Windows (elevated helper)

    /// <summary>Entry point of the elevated helper: <c>--flash-helper &lt;image&gt; &lt;disk number&gt; &lt;progress file&gt;</c>.</summary>
    public static int RunElevatedHelper(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 4)
            return 2;

        var progressFile = args[3];
        try
        {
            WriteWindowsDisk(args[1], int.Parse(args[2], CultureInfo.InvariantCulture), progressFile);
            TryWrite(progressFile, "1");
            return 0;
        }
        catch (Exception exception)
        {
            TryWrite(progressFile, "error " + exception.Message);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindowsDisk(string image, int diskNumber, string progressFile)
    {
        // Lock and dismount every volume of the disk; the handles stay open while writing.
        var volumes = Windows.LockVolumesOfDisk(diskNumber);
        try
        {
            using var disk = Windows.OpenDisk(diskNumber);
            var cancelFile = progressFile + ".cancel";

            // Wipe the partition table first, so Windows doesn't mount the new volume mid-write,
            // write everything after the first MiB, then the first MiB last.
            disk.Position = 0;
            disk.Write(new byte[1 << 20]);
            disk.Flush();
            Windows.UpdateProperties(disk.SafeFileHandle);

            var head = new byte[1 << 20];
            var headLength = 0;
            using var output = new WindowsDiskWriter(disk, head, length => headLength = length);
            Decompress(image, output, new Progress<double>(f =>
            {
                TryWrite(progressFile, f.ToString("0.000", CultureInfo.InvariantCulture));
                if (File.Exists(cancelFile))
                    throw new OperationCanceledException("Cancelled.");
            }), CancellationToken.None);
            output.Finish();

            disk.Position = 0;
            disk.Write(head, 0, AlignUp(headLength, 512));
            disk.Flush();
            Windows.UpdateProperties(disk.SafeFileHandle);
        }
        finally
        {
            foreach (var volume in volumes)
                volume.Dispose();
        }
    }

    /// <summary>Writes from offset 1 MiB, keeping the first MiB aside; sector aligned writes only.</summary>
    private sealed class WindowsDiskWriter(FileStream disk, byte[] head, Action<int> headWritten) : Stream
    {
        private readonly byte[] _pending = new byte[ChunkSize];
        private int _pendingLength;
        private long _position;

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                if (_position < head.Length)
                {
                    var take = (int)Math.Min(count, head.Length - _position);
                    Array.Copy(buffer, offset, head, _position, take);
                    _position += take;
                    headWritten((int)_position);
                    offset += take;
                    count -= take;
                    if (_position == head.Length)
                        disk.Position = head.Length;
                    continue;
                }

                var copy = Math.Min(count, _pending.Length - _pendingLength);
                Array.Copy(buffer, offset, _pending, _pendingLength, copy);
                _pendingLength += copy;
                _position += copy;
                offset += copy;
                count -= copy;
                if (_pendingLength == _pending.Length)
                    FlushPending();
            }
        }

        public void Finish()
        {
            if (_pendingLength > 0)
            {
                var aligned = AlignUp(_pendingLength, 512);
                Array.Clear(_pending, _pendingLength, aligned - _pendingLength);
                _pendingLength = aligned;
                FlushPending();
            }

            disk.Flush();
        }

        private void FlushPending()
        {
            disk.Write(_pending, 0, _pendingLength);
            _pendingLength = 0;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [SupportedOSPlatform("windows")]
    private static class Windows
    {
        private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
        private const uint ShareReadWrite = 0x3, OpenExisting = 3;
        private const uint FileFlagWriteThrough = 0x80000000;
        private const uint FsctlLockVolume = 0x00090018, FsctlDismountVolume = 0x00090020;
        private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
        private const uint IoctlDiskUpdateProperties = 0x00070140;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize,
            byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);

        public static FileStream OpenDisk(int number)
        {
            var handle = CreateFile($@"\\.\PhysicalDrive{number}", GenericRead | GenericWrite, ShareReadWrite,
                IntPtr.Zero, OpenExisting, FileFlagWriteThrough, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new IOException($"Could not open disk {number} (error {Marshal.GetLastWin32Error()}).");
            return new FileStream(handle, FileAccess.ReadWrite, 0);
        }

        public static void UpdateProperties(SafeFileHandle disk) =>
            DeviceIoControl(disk, IoctlDiskUpdateProperties, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero);

        public static System.Collections.Generic.List<SafeFileHandle> LockVolumesOfDisk(int number)
        {
            var locked = new System.Collections.Generic.List<SafeFileHandle>();
            for (var letter = 'A'; letter <= 'Z'; letter++)
            {
                var volume = CreateFile($@"\\.\{letter}:", GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero,
                    OpenExisting, 0, IntPtr.Zero);
                if (volume.IsInvalid)
                    continue;

                if (!IsOnDisk(volume, number))
                {
                    volume.Dispose();
                    continue;
                }

                if (!DeviceIoControl(volume, FsctlLockVolume, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero))
                {
                    volume.Dispose();
                    throw new IOException($"Drive {letter}: is in use, close the programs using it and try again.");
                }

                DeviceIoControl(volume, FsctlDismountVolume, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero);
                locked.Add(volume);
            }

            return locked;
        }

        private static bool IsOnDisk(SafeFileHandle volume, int number)
        {
            // VOLUME_DISK_EXTENTS: count (4) + padding (4) + extents of { disk number (4), pad (4), offset (8), length (8) }
            var buffer = new byte[8 + 24 * 8];
            if (!DeviceIoControl(volume, IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, buffer, (uint)buffer.Length,
                    out _, IntPtr.Zero))
                return false;

            var count = BitConverter.ToInt32(buffer, 0);
            for (var i = 0; i < count && i < 8; i++)
            {
                if (BitConverter.ToInt32(buffer, 8 + i * 24) == number)
                    return true;
            }

            return false;
        }
    }

    // ------------------------------------------------------------ shared

    private static void Decompress(string image, Stream output, IProgress<double> progress, CancellationToken ct)
    {
        ImageBuilder.EnsureXz();

        using var input = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var xz = new XZStream(input, new XZDecompressOptions { LeaveOpen = true });
        var buffer = new byte[ChunkSize];
        var lastReport = Stopwatch.StartNew();
        int read;
        while ((read = xz.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            if (lastReport.ElapsedMilliseconds >= 200)
            {
                lastReport.Restart();
                progress.Report((double)input.Position / input.Length);
            }
        }

        output.Flush();
        progress.Report(1);
    }

    private static int AlignUp(int value, int unit) => (value + unit - 1) / unit * unit;

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void TryWrite(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text);
        }
        catch (IOException)
        {
            // progress is best effort
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGcCreator.Services;

/// <summary>A whole USB disk that can receive a card image.</summary>
/// <param name="Device">Linux: <c>/dev/sdX</c>. Windows: the disk number.</param>
public sealed record UsbDrive(string Device, string Model, long Size, IReadOnlyList<string> MountPoints)
{
    public string SizeText => FormatSize(Size);

    public string Title => $"{Model}  ·  {SizeText}";

    public string Detail => OperatingSystem.IsWindows()
        ? $"Disk {Device}{(MountPoints.Count > 0 ? "  ·  " + string.Join(", ", MountPoints) : string.Empty)}"
        : $"{Device}{(MountPoints.Count > 0 ? "  ·  " + string.Join(", ", MountPoints) : string.Empty)}";

    public static string FormatSize(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        return bytes >= gb ? $"{bytes / gb:0.0} GB" : bytes >= mb ? $"{bytes / mb:0} MB" : $"{bytes / kb:0} KB";
    }
}

/// <summary>Lists the USB disks. System disks are never returned.</summary>
public static class UsbDrives
{
    private static readonly string[] SystemMounts = ["/", "/boot", "/boot/efi", "/home", "/usr", "/var"];

    public static Task<IReadOnlyList<UsbDrive>> ListAsync(CancellationToken ct = default) =>
        OperatingSystem.IsWindows() ? ListWindowsAsync(ct)
        : OperatingSystem.IsLinux() ? ListLinuxAsync(ct)
        : Task.FromResult<IReadOnlyList<UsbDrive>>([]);

    // ------------------------------------------------------------ Linux

    private static async Task<IReadOnlyList<UsbDrive>> ListLinuxAsync(CancellationToken ct)
    {
        var json = await RunAsync("lsblk", ["-J", "-b", "-p", "-o", "NAME,TYPE,TRAN,SIZE,VENDOR,MODEL,MOUNTPOINTS"], ct);
        using var document = JsonDocument.Parse(json);

        var drives = new List<UsbDrive>();
        foreach (var disk in document.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            if (Text(disk, "type") != "disk" || Text(disk, "tran") != "usb")
                continue;

            var mounts = new List<string>();
            CollectMounts(disk, mounts);
            if (mounts.Any(m => SystemMounts.Contains(m)))
                continue;

            var model = string.Join(' ', new[] { Text(disk, "vendor"), Text(disk, "model") }
                .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            drives.Add(new UsbDrive(Text(disk, "name"), model.Length > 0 ? model : "USB drive",
                Number(disk, "size"), mounts));
        }

        return drives;
    }

    private static void CollectMounts(JsonElement device, List<string> mounts)
    {
        if (device.TryGetProperty("mountpoints", out var points) && points.ValueKind == JsonValueKind.Array)
            mounts.AddRange(points.EnumerateArray().Select(p => p.GetString()).OfType<string>());

        if (device.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                CollectMounts(child, mounts);
    }

    /// <summary>Mounted partitions of a Linux disk (to unmount before writing).</summary>
    public static async Task<IReadOnlyList<string>> GetMountedPartitionsAsync(string disk, CancellationToken ct)
    {
        var json = await RunAsync("lsblk", ["-J", "-p", "-o", "NAME,MOUNTPOINTS", disk], ct);
        using var document = JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var device in document.RootElement.GetProperty("blockdevices").EnumerateArray())
            Walk(device);
        return result;

        void Walk(JsonElement device)
        {
            if (device.TryGetProperty("mountpoints", out var points) && points.ValueKind == JsonValueKind.Array &&
                points.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String))
                result.Add(Text(device, "name"));

            if (device.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var child in children.EnumerateArray())
                    Walk(child);
        }
    }

    // ------------------------------------------------------------ Windows

    private static async Task<IReadOnlyList<UsbDrive>> ListWindowsAsync(CancellationToken ct)
    {
        const string script =
            "$ErrorActionPreference='SilentlyContinue';" +
            "@(Get-Disk | Where-Object { $_.BusType -eq 'USB' -and -not $_.IsSystem -and -not $_.IsBoot } | ForEach-Object {" +
            " $letters = @(Get-Partition -DiskNumber $_.Number | Where-Object DriveLetter | ForEach-Object { \"$($_.DriveLetter):\" });" +
            " [pscustomobject]@{ Number = $_.Number; Model = $_.FriendlyName; Size = [int64]$_.Size; Letters = $letters } })" +
            " | ConvertTo-Json -Compress -Depth 3";

        var json = await RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], ct);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToList()
            : [document.RootElement];

        return items.Select(d => new UsbDrive(
                Device: d.GetProperty("Number").GetRawText(),
                Model: Text(d, "Model") is { Length: > 0 } model ? model : "USB drive",
                Size: Number(d, "Size"),
                MountPoints: d.TryGetProperty("Letters", out var letters) && letters.ValueKind == JsonValueKind.Array
                    ? letters.EnumerateArray().Select(l => l.GetString()).OfType<string>().ToList()
                    : []))
            .ToList();
    }

    // ------------------------------------------------------------ helpers

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : string.Empty;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Number ? value.GetInt64()
            : long.TryParse(value.GetString(), out var parsed) ? parsed : 0
            : 0;

    internal static async Task<string> RunAsync(string file, IEnumerable<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        info.Environment["LC_ALL"] = "C";

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} failed: {(await error).Trim()}");
        return await output;
    }
}

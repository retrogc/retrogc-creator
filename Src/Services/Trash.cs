using System;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace RetroGcCreator.Services;

/// <summary>
/// Removes files / folders the undoable way: Windows Recycle Bin, Linux desktop trash (<c>gio trash</c>).
/// Only when no trash is available (Linux without gio) are they deleted for good.
/// </summary>
public static class Trash
{
    public static void MoveToTrash(string path)
    {
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            return;

        if (OperatingSystem.IsWindows())
        {
            if (isDirectory)
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            Log.Info($"Moved to the Recycle Bin: {path}");
            return;
        }

        if (TryGioTrash(path))
        {
            Log.Info($"Moved to the trash: {path}");
            return;
        }

        if (isDirectory)
            Directory.Delete(path, recursive: true);
        else
            File.Delete(path);
        Log.Info($"Deleted (no trash available): {path}");
    }

    private static bool TryGioTrash(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gio")
            {
                ArgumentList = { "trash", "--", path },
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });
            if (process is null)
                return false;

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0 && !File.Exists(path) && !Directory.Exists(path);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false; // gio not installed
        }
    }
}

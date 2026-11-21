using System;

namespace RetroGcCreator.Services;

/// <summary>
/// Minimal console logger with timestamps.
/// </summary>
public static class Log
{
    public static void Info(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    public static void Error(string message, Exception? exception = null) =>
        Console.Error.WriteLine(exception is null
            ? $"[{DateTime.Now:HH:mm:ss}] ERROR: {message}"
            : $"[{DateTime.Now:HH:mm:ss}] ERROR: {message}: {exception.Message}");
}

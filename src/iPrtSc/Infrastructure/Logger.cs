using System;
using System.IO;

namespace iPrtSc;

/// <summary>Minimal file logger at %AppData%\iPrtSc\log.txt for diagnostics.</summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static readonly string Path =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "iPrtSc", "log.txt");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }

    public static void Log(string context, Exception ex) =>
        Log($"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
}

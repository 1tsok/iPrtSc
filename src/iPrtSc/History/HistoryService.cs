using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace iPrtSc;

/// <summary>
/// Keeps a rolling archive of every capture under %APPDATA%\iPrtSc\history so the
/// tray "History" flyout can offer recent shots. Files older than the configured
/// retention are pruned at startup. All operations are best-effort and silent.
/// </summary>
public static class HistoryService
{
    public static string HistoryFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iPrtSc", "history");

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

    /// <summary>
    /// Saves a lossless PNG copy of a capture. Called for every completed capture
    /// (Copy or Save). No-op when history is disabled (retention 0).
    /// </summary>
    public static void Archive(BitmapSource src, AppSettings s)
    {
        if (s.HistoryRetentionDays <= 0) return;
        try
        {
            Directory.CreateDirectory(HistoryFolder);
            var path = UniquePath();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        catch (Exception ex) { Logger.Log("HistoryService.Archive", ex); }
    }

    /// <summary>Deletes history files older than <paramref name="days"/>. No-op when disabled.</summary>
    public static void Prune(int days)
    {
        if (days <= 0) return;
        try
        {
            if (!Directory.Exists(HistoryFolder)) return;
            var cutoff = DateTime.Now.AddDays(-days);
            foreach (var file in Directory.EnumerateFiles(HistoryFolder))
            {
                try
                {
                    if (IsImage(file) && File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch { /* skip locked / in-use files */ }
            }
        }
        catch (Exception ex) { Logger.Log("HistoryService.Prune", ex); }
    }

    /// <summary>Most-recently-modified history files first, capped at <paramref name="count"/>.</summary>
    public static IReadOnlyList<string> Recent(int count)
    {
        try
        {
            if (!Directory.Exists(HistoryFolder)) return Array.Empty<string>();
            return Directory.EnumerateFiles(HistoryFolder)
                .Where(IsImage)
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Take(count)
                .Select(fi => fi.FullName)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Log("HistoryService.Recent", ex);
            return Array.Empty<string>();
        }
    }

    private static string UniquePath()
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var path = Path.Combine(HistoryFolder, $"iPrtSc_{stamp}.png");
        int n = 1;
        while (File.Exists(path)) // two captures in the same second
            path = Path.Combine(HistoryFolder, $"iPrtSc_{stamp}_{n++}.png");
        return path;
    }

    private static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}

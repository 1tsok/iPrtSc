using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace iPrtSc;

public static class SaveService
{
    /// <summary>Saves a WPF image source to a path, choosing the encoder from the extension.</summary>
    public static void SaveSource(System.Windows.Media.Imaging.BitmapSource src, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        BitmapEncoder enc = ext is ".jpg" or ".jpeg" ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Default target folder (created if missing). Pictures\iPrtSc unless overridden.</summary>
    public static string DefaultFolder(AppSettings s)
    {
        var folder = string.IsNullOrWhiteSpace(s.SaveFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "iPrtSc")
            : s.SaveFolder!;
        try { Directory.CreateDirectory(folder); } catch { /* best effort */ }
        return folder;
    }

    /// <summary>Suggested timestamped file name with the configured default extension.</summary>
    public static string DefaultFileName(AppSettings s)
    {
        bool jpg = IsJpeg(s.SaveFormat);
        return $"iPrtSc_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{(jpg ? "jpg" : "png")}";
    }

    /// <summary>Saves to an explicit path, choosing format from the file extension.</summary>
    public static void SaveTo(Bitmap bmp, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var fmt = ext is ".jpg" or ".jpeg" ? ImageFormat.Jpeg : ImageFormat.Png;
        bmp.Save(path, fmt);
    }

    /// <summary>Quick-save to the default folder with an auto-generated name; returns the path.</summary>
    public static string QuickSave(Bitmap bmp, AppSettings s)
    {
        var path = Path.Combine(DefaultFolder(s), DefaultFileName(s));
        SaveTo(bmp, path);
        return path;
    }

    private static bool IsJpeg(string format) =>
        format.Equals("Jpeg", StringComparison.OrdinalIgnoreCase) ||
        format.Equals("Jpg", StringComparison.OrdinalIgnoreCase);
}

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace iPrtSc;

public static class SaveService
{
    /// <summary>
    /// JPEG quality for saved shots. The encoder default is 75, which rings visibly around UI
    /// text and thin annotation strokes, the two things a screenshot is usually made of. 92 is
    /// hard to tell from lossless on this content and still far smaller than PNG.
    /// </summary>
    private const int JpegQuality = 92;

    /// <summary>Saves a WPF image source to a path, choosing the encoder from the extension.</summary>
    public static void SaveSource(System.Windows.Media.Imaging.BitmapSource src, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        // QualityLevel is set before the frame is added: the encoder reads it when it writes.
        BitmapEncoder enc = ext is ".jpg" or ".jpeg"
            ? new JpegBitmapEncoder { QualityLevel = JpegQuality }
            : new PngBitmapEncoder();
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
        if (ext is not (".jpg" or ".jpeg"))
        {
            bmp.Save(path, ImageFormat.Png);
            return;
        }

        // Save(path, ImageFormat.Jpeg) always writes at quality 75; GDI+ takes a quality only
        // through the codec overload, so the encoder has to be looked up by format GUID.
        var codec = JpegCodec();
        if (codec == null)
        {
            bmp.Save(path, ImageFormat.Jpeg);
            return;
        }

        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(Encoder.Quality, (long)JpegQuality);
        bmp.Save(path, codec, ps);
    }

    /// <summary>The installed GDI+ JPEG encoder, or null if it is somehow missing.</summary>
    private static ImageCodecInfo? JpegCodec()
    {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
        return null;
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

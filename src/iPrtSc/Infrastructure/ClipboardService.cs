using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace iPrtSc;

public static class ClipboardService
{
    /// <summary>Puts a WPF image source on the clipboard (PNG + standard bitmap).</summary>
    public static void CopyImage(BitmapSource src)
    {
        var png = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        enc.Save(png);

        var data = new Forms.DataObject();
        png.Position = 0;
        data.SetData("PNG", autoConvert: false, png);

        using var bmp = (Bitmap)Image.FromStream(new MemoryStream(png.ToArray()));
        data.SetData(Forms.DataFormats.Bitmap, autoConvert: true, (Bitmap)bmp.Clone());

        png.Position = 0;
        Forms.Clipboard.SetDataObject(data, copy: true);
    }

    /// <summary>
    /// Puts the image on the clipboard in both PNG and standard bitmap formats so
    /// browsers, Office and chat apps can all paste it.
    /// </summary>
    public static void CopyImage(Bitmap bmp)
    {
        var data = new Forms.DataObject();

        var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        data.SetData("PNG", autoConvert: false, png);

        data.SetData(Forms.DataFormats.Bitmap, autoConvert: true, (Bitmap)bmp.Clone());

        Forms.Clipboard.SetDataObject(data, copy: true);
    }

    /// <summary>Puts plain text on the clipboard (e.g. OCR output). No-op for empty text.</summary>
    public static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Forms.Clipboard.SetText(text);
    }
}

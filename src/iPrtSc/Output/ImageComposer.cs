using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace iPrtSc;

public static class ImageComposer
{
    /// <summary>
    /// Alpha-composites a premultiplied (Pbgra32) overlay over an opaque photo,
    /// touching only the pixels the overlay actually covers. Output is opaque Bgra32.
    /// </summary>
    public static BitmapSource Over(BitmapSource photo, BitmapSource overlay)
    {
        int w = photo.PixelWidth, h = photo.PixelHeight;
        int stride = w * 4;

        var baseBgra = new FormatConvertedBitmap(photo, PixelFormats.Bgra32, null, 0);
        var over = overlay.Format == PixelFormats.Pbgra32
            ? overlay
            : new FormatConvertedBitmap(overlay, PixelFormats.Pbgra32, null, 0);

        var bp = new byte[h * stride];
        var op = new byte[h * stride];
        baseBgra.CopyPixels(bp, stride, 0);
        over.CopyPixels(op, stride, 0);

        for (int i = 0; i < bp.Length; i += 4)
        {
            int a = op[i + 3];
            if (a == 0) continue;          // overlay transparent here → keep photo pixel
            int inv = 255 - a;
            // overlay channels are premultiplied, so: out = over + photo * (1 - a)
            bp[i]     = (byte)(op[i]     + bp[i]     * inv / 255);
            bp[i + 1] = (byte)(op[i + 1] + bp[i + 1] * inv / 255);
            bp[i + 2] = (byte)(op[i + 2] + bp[i + 2] * inv / 255);
            bp[i + 3] = 255;
        }

        var outBmp = BitmapSource.Create(w, h, photo.DpiX, photo.DpiY,
            PixelFormats.Bgra32, null, bp, stride);
        outBmp.Freeze();
        return outBmp;
    }
}

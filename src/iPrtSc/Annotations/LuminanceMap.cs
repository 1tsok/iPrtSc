using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace iPrtSc;

/// <summary>
/// A downscaled Gray8 copy of a capture, answering how bright the background is under a
/// given box. Built on first use, since most captures never ask.
/// </summary>
public sealed class LuminanceMap
{
    private const int Shift = 3;   // the map is kept at 1/8 resolution

    private readonly BitmapSource _src;
    private readonly double _scale;   // DIP → source pixels
    private byte[]? _map;
    private int _w, _h;

    public LuminanceMap(BitmapSource src, double scale)
    {
        _src = src;
        _scale = scale;
    }

    private void Ensure()
    {
        if (_map != null) return;
        double f = 1.0 / (1 << Shift);
        var gray = new FormatConvertedBitmap(
            new TransformedBitmap(_src, new ScaleTransform(f, f)), PixelFormats.Gray8, null, 0);
        _w = gray.PixelWidth;
        _h = gray.PixelHeight;
        _map = new byte[_w * _h];
        gray.CopyPixels(_map, _w, 0);
    }

    /// <summary>Mean luminance (0–255) under an unrotated box, given in overlay DIP.</summary>
    public double Mean(Point center, Vector half)
    {
        Ensure();
        double f = _scale / (1 << Shift);
        int x0 = Math.Clamp((int)((center.X - half.X) * f), 0, _w - 1);
        int x1 = Math.Clamp((int)((center.X + half.X) * f), x0, _w - 1);
        int y0 = Math.Clamp((int)((center.Y - half.Y) * f), 0, _h - 1);
        int y1 = Math.Clamp((int)((center.Y + half.Y) * f), y0, _h - 1);
        long sum = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) sum += _map![y * _w + x];
        return (double)sum / ((x1 - x0 + 1) * (y1 - y0 + 1));
    }
}

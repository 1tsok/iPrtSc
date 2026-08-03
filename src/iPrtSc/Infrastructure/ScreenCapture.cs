using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace iPrtSc;

public static class ScreenCapture
{
    /// <summary>Captures the whole virtual desktop (all monitors) in physical pixels.</summary>
    public static (Bitmap bmp, BitmapSource src, Rectangle bounds) CaptureVirtualScreen()
    {
        var vb = GetVirtualBounds();
        var bmp = new Bitmap(vb.Width, vb.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(vb.Left, vb.Top, 0, 0, new Size(vb.Width, vb.Height), CopyPixelOperation.SourceCopy);
        }
        ForceOpaque(bmp);
        return (bmp, ToBitmapSource(bmp), vb);
    }

    /// <summary>
    /// Captures the single monitor the cursor is on, in physical pixels. Used by the
    /// selection-free full screen copy; falls back to the whole virtual desktop if the
    /// cursor position or the monitor layout can't be read.
    /// </summary>
    public static (Bitmap bmp, BitmapSource src, Rectangle bounds) CaptureMonitorAtCursor()
    {
        var mb = GetMonitorBoundsAtCursor();
        var bmp = new Bitmap(mb.Width, mb.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(mb.Left, mb.Top, 0, 0, new Size(mb.Width, mb.Height), CopyPixelOperation.SourceCopy);
        }
        ForceOpaque(bmp);
        return (bmp, ToBitmapSource(bmp), mb);
    }

    /// <summary>Bounds of the monitor holding the cursor, in physical pixels.</summary>
    public static Rectangle GetMonitorBoundsAtCursor()
    {
        if (!NativeMethods.GetCursorPos(out var pt))
            return GetVirtualBounds();

        Rectangle? hit = null;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT _, IntPtr _) =>
            {
                var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfo(hMon, ref mi))
                {
                    var m = mi.rcMonitor;
                    // Right/Bottom are exclusive, so a cursor on the far edge belongs to the next monitor.
                    if (pt.X >= m.Left && pt.X < m.Right && pt.Y >= m.Top && pt.Y < m.Bottom)
                    {
                        hit = new Rectangle(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top);
                        return false; // found it, stop enumerating
                    }
                }
                return true;
            }, IntPtr.Zero);

        return hit ?? GetVirtualBounds();
    }

    /// <summary>Union of all monitor rectangles in physical pixels.</summary>
    public static Rectangle GetVirtualBounds()
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        bool any = false;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT _, IntPtr _) =>
            {
                var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfo(hMon, ref mi))
                {
                    var m = mi.rcMonitor;
                    if (m.Left < minX) minX = m.Left;
                    if (m.Top < minY) minY = m.Top;
                    if (m.Right > maxX) maxX = m.Right;
                    if (m.Bottom > maxY) maxY = m.Bottom;
                    any = true;
                }
                return true;
            }, IntPtr.Zero);

        if (!any)
        {
            var vs = Forms.SystemInformation.VirtualScreen;
            return new Rectangle(vs.Left, vs.Top, vs.Width, vs.Height);
        }
        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    public static Bitmap Crop(Bitmap src, Rectangle r)
    {
        var dst = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
        return dst;
    }

    /// <summary>
    /// Forces every pixel fully opaque. CopyFromScreen writes RGB but leaves the alpha
    /// channel undefined — on parts of the desktop it comes back below 255, so those
    /// pixels are semi-transparent. They look fine over the dark overlay, but in a saved
    /// PNG a viewer composites them over white and they turn into bright speckles
    /// (most visible on dark photos). Setting alpha to 255 keeps the captured RGB intact.
    /// </summary>
    private static unsafe void ForceOpaque(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < data.Height; y++)
            {
                byte* px = (byte*)data.Scan0 + (long)y * data.Stride;
                for (int x = 0; x < data.Width; x++, px += 4)
                    px[3] = 255;
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}

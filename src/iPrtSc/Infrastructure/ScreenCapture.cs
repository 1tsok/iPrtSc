using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
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

    /// <summary>Physical-pixel bounds of the monitor under the cursor (nearest if outside all).</summary>
    public static Rectangle CursorMonitorBounds()
    {
        if (NativeMethods.GetCursorPos(out var pt))
        {
            var hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfo(hMon, ref mi))
            {
                var m = mi.rcMonitor;
                return new Rectangle(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top);
            }
        }
        return GetVirtualBounds();
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
    private static void ForceOpaque(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * bmp.Height;
            var buf = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);
            for (int i = 3; i < bytes; i += 4) buf[i] = 255;
            Marshal.Copy(buf, 0, data.Scan0, bytes);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        var h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                h, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            NativeMethods.DeleteObject(h);
        }
    }
}

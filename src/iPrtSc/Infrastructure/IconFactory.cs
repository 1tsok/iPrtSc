using System.Drawing;
using System.Drawing.Drawing2D;

namespace iPrtSc;

/// <summary>Draws the tray icon at runtime so no binary asset is needed.</summary>
public static class IconFactory
{
    /// <summary>
    /// Draws the tray icon. When <paramref name="updateBadge"/> is set, an orange
    /// dot is overlaid in the bottom-right corner to signal that a newer version
    /// is available.
    /// </summary>
    public static Icon CreateAppIcon(bool updateBadge = false)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Subtle shadow pass — gives visibility on light taskbars.
            using (var shadow = new Pen(Color.FromArgb(90, 0, 0, 0), 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                DrawCorners(g, shadow, 4.5f, 9f, 32f);

            // White line-art brackets (same round-cap style as toolbar icons).
            using (var pen = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                DrawCorners(g, pen, 4f, 9f, 32f);

            if (updateBadge)
                DrawUpdateBadge(g, 32f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static void DrawUpdateBadge(Graphics g, float sz)
    {
        const float d = 13f;                 // dot diameter
        float x = sz - d - 1f, y = sz - d - 1f;

        // Dark halo so the dot reads on any bracket/taskbar colour.
        using (var halo = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            g.FillEllipse(halo, x - 1.5f, y - 1.5f, d + 3f, d + 3f);

        using var dot = new SolidBrush(Color.FromArgb(255, 255, 149, 0)); // iOS-style orange
        g.FillEllipse(dot, x, y, d, d);
    }

    private static void DrawCorners(Graphics g, Pen p, float m, float a, float sz)
    {
        g.DrawLines(p, new[] { new PointF(m + a, m),      new PointF(m,      m),      new PointF(m,      m + a) }); // TL
        g.DrawLines(p, new[] { new PointF(sz-m-a, m),     new PointF(sz-m,   m),      new PointF(sz-m,   m + a) }); // TR
        g.DrawLines(p, new[] { new PointF(m,      sz-m-a),new PointF(m,      sz-m),   new PointF(m + a,  sz-m)  }); // BL
        g.DrawLines(p, new[] { new PointF(sz-m,   sz-m-a),new PointF(sz-m,   sz-m),   new PointF(sz-m-a, sz-m)  }); // BR
    }
}

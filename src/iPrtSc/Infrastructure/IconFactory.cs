using System.Drawing;
using System.Drawing.Drawing2D;

namespace iPrtSc;

/// <summary>Draws the tray icon at runtime so no binary asset is needed.</summary>
public static class IconFactory
{
    public static Icon CreateAppIcon()
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
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static void DrawCorners(Graphics g, Pen p, float m, float a, float sz)
    {
        g.DrawLines(p, new[] { new PointF(m + a, m),      new PointF(m,      m),      new PointF(m,      m + a) }); // TL
        g.DrawLines(p, new[] { new PointF(sz-m-a, m),     new PointF(sz-m,   m),      new PointF(sz-m,   m + a) }); // TR
        g.DrawLines(p, new[] { new PointF(m,      sz-m-a),new PointF(m,      sz-m),   new PointF(m + a,  sz-m)  }); // BL
        g.DrawLines(p, new[] { new PointF(sz-m,   sz-m-a),new PointF(sz-m,   sz-m),   new PointF(sz-m-a, sz-m)  }); // BR
    }
}

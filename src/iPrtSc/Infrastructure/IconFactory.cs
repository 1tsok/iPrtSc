using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace iPrtSc;

/// <summary>Builds the tray icon from the app logo (Assets/icon-256.png).</summary>
public static class IconFactory
{
    private static Bitmap? _logo;

    /// <summary>
    /// Renders the tray icon from the app logo, recoloured white for taskbar
    /// contrast. When <paramref name="updateBadge"/> is set, an orange dot is
    /// overlaid in the bottom-right corner to signal a newer version.
    /// </summary>
    public static Icon CreateAppIcon(bool updateBadge = false)
    {
        var logo = Logo();

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            var dst = new Rectangle(0, 0, 32, 32);

            // Subtle drop shadow (offset, semi-transparent black) for visibility on light taskbars.
            DrawTinted(g, logo, new Rectangle(1, 1, 32, 32), ShadowMatrix);

            // The logo itself, recoloured solid white via the alpha channel.
            DrawTinted(g, logo, dst, WhiteMatrix);

            if (updateBadge)
                DrawUpdateBadge(g, 32f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Loads and caches the logo bitmap from the packed resource.</summary>
    private static Bitmap Logo()
    {
        if (_logo != null) return _logo;
        var uri = new Uri("pack://application:,,,/Assets/icon-256.png");
        using var stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
        _logo = new Bitmap(stream);
        return _logo;
    }

    private static void DrawTinted(Graphics g, Image src, Rectangle dst, ColorMatrix matrix)
    {
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(matrix);
        g.DrawImage(src, dst, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
    }

    // Forces every pixel to white while preserving the source alpha.
    private static readonly ColorMatrix WhiteMatrix = new(new[]
    {
        new float[] { 0, 0, 0, 0, 0 },
        new float[] { 0, 0, 0, 0, 0 },
        new float[] { 0, 0, 0, 0, 0 },
        new float[] { 0, 0, 0, 1, 0 },
        new float[] { 1, 1, 1, 0, 1 },
    });

    // Forces black and dims the alpha to a soft shadow.
    private static readonly ColorMatrix ShadowMatrix = new(new[]
    {
        new float[] { 0, 0, 0, 0,     0 },
        new float[] { 0, 0, 0, 0,     0 },
        new float[] { 0, 0, 0, 0,     0 },
        new float[] { 0, 0, 0, 0.45f, 0 },
        new float[] { 0, 0, 0, 0,     1 },
    });

    private static void DrawUpdateBadge(Graphics g, float sz)
    {
        const float dot  = 13f;   // orange dot diameter
        const float edge = 0.5f;  // keep the badge clear of the icon edge so it isn't clipped

        float ox = sz - edge - dot, oy = ox;

        using var fill = new SolidBrush(Color.FromArgb(255, 255, 149, 0)); // iOS-style orange
        g.FillEllipse(fill, ox, oy, dot, dot);
    }
}

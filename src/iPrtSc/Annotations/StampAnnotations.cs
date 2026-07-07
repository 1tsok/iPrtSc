using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace iPrtSc;

/// <summary>A stamp design: text, ink color and natural (unscaled) size in DIP.</summary>
public sealed record StampDef(string Id, string Text, string Ink, double W, double H,
    double FontSize, string? Icon = null, bool Circle = false);

/// <summary>
/// The built-in stamp set and its visual factory. Stamps mimic worn rubber prints:
/// a thick outer + thin inner border with dash gaps for the "bad ink" look, heavy
/// condensed capitals, and slightly translucent ink.
/// </summary>
public static class StampCatalog
{
    private static readonly FontFamily HeavyFont = new("Arial Black, Segoe UI Black, Segoe UI");

    /// <summary>Uniform gap between the text block and the outer border of rect stamps.</summary>
    private const double PadX = 21;

    public static readonly StampDef[] All =
    {
        Rect("approved",     "APPROVED",     "#2E7D32", 58, 25),
        Rect("rejected",     "REJECTED",     "#C62828", 58, 25),
        Rect("draft",        "DRAFT",        "#5F6368", 58, 25),
        Rect("like",         "LIKE",         "#1565C0", 58, 25, "thumb"),
        Rect("paid",         "PAID",         "#4527A0", 64, 31),
        Rect("topsecret",    "TOP SECRET",   "#C62828", 54, 23),
        Rect("confidential", "CONFIDENTIAL", "#37474F", 50, 20),
        Rect("done",         "DONE",         "#00695C", 58, 25, "check"),
        Rect("wow",          "WOW!",         "#E65100", 62, 29),
        Rect("fixed",        "FIXED",        "#2E7D32", 58, 25),
        Rect("bug",          "BUG",          "#C62828", 58, 25, "bug"),
        Rect("new",          "NEW",          "#C2185B", 60, 27),
        new("thankyou",      "THANK YOU",    "#7E57C2", 130, 130, 13, Circle: true),
    };

    /// <summary>Width comes from the measured text so side padding is identical everywhere.</summary>
    private static StampDef Rect(string id, string text, string ink, double h, double fontSize,
        string? icon = null)
    {
        double iconW = icon switch { "thumb" => 36, "check" => 31, "bug" => 40, _ => 0 };
        double w = Math.Ceiling(TextWidth(text, fontSize) + iconW + 2 * PadX);
        return new StampDef(id, text, ink, w, h, fontSize, icon);
    }

    /// <summary>
    /// Bright counterparts of the deep inks, used on dark backgrounds. Lighter tones of
    /// the same hues (Material dark-theme style) so a stamp keeps its identity but reads
    /// clearly over dark screenshots. Keyed by the deep ink since several stamps share one.
    /// </summary>
    private static readonly Dictionary<string, string> BrightInk = new()
    {
        ["#2E7D32"] = "#81C784",   // green
        ["#C62828"] = "#E57373",   // red
        ["#5F6368"] = "#BDC1C6",   // grey
        ["#1565C0"] = "#64B5F6",   // blue
        ["#4527A0"] = "#B39DDB",   // deep purple
        ["#37474F"] = "#90A4AE",   // blue grey
        ["#00695C"] = "#4DB6AC",   // teal
        ["#E65100"] = "#FFB74D",   // orange
        ["#C2185B"] = "#F06292",   // pink
        ["#7E57C2"] = "#B39DDB",   // light purple
    };

    public static string Ink(StampDef def, bool bright) =>
        bright && BrightInk.TryGetValue(def.Ink, out var b) ? b : def.Ink;

    private static double TextWidth(string text, double fontSize)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(HeavyFont, FontStyles.Normal, FontWeights.Black, FontStretches.Normal),
            fontSize, Brushes.Black, 1.0);
        return ft.WidthIncludingTrailingWhitespace;
    }

    private static readonly Brush GrungeMask = CreateGrungeMask();

    /// <summary>
    /// Tiled noise opacity mask: tiny fully-worn holes plus faded patches, so the ink
    /// prints unevenly like a real rubber stamp. Fixed seed keeps the wear stable
    /// between sessions and between the palette preview and the placed stamp.
    /// </summary>
    private static Brush CreateGrungeMask()
    {
        const int size = 96;
        var rnd = new Random(42);
        var px = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            double v = rnd.NextDouble();
            byte a = v < 0.05 ? (byte)0                          // missing ink
                   : v < 0.17 ? (byte)(70 + rnd.Next(90))        // faded patch
                   : (byte)(215 + rnd.Next(41));                 // solid print
            int o = i * 4;
            px[o] = px[o + 1] = px[o + 2] = 255;
            px[o + 3] = a;
        }
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
        bmp.Freeze();
        // Viewport slightly larger than the bitmap so each noise pixel spans ~1.4 DIP.
        var brush = new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 132, 132),
            ViewportUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }

    public static FrameworkElement BuildVisual(StampDef def, bool brightInk = false)
    {
        var ink = (Brush)new BrushConverter().ConvertFromString(Ink(def, brightInk))!;
        ink.Freeze();
        return def.Circle ? BuildCircle(def, ink) : BuildRect(def, ink);
    }

    /// <summary>WPF dash values are multiples of the stroke thickness; authored in px.</summary>
    private static DoubleCollection Dash(double thickness, params double[] px)
    {
        var dc = new DoubleCollection();
        foreach (var v in px) dc.Add(v / thickness);
        return dc;
    }

    private static FrameworkElement BuildRect(StampDef def, Brush ink)
    {
        // Transparent background so the Move tool can grab the stamp anywhere inside it.
        var root = new Grid
        {
            Width = def.W, Height = def.H,
            Background = Brushes.Transparent, OpacityMask = GrungeMask
        };
        root.Children.Add(new Rectangle
        {
            Stroke = ink, StrokeThickness = 3.5, RadiusX = 7, RadiusY = 7,
            StrokeDashArray = Dash(3.5, 44, 2, 61, 3, 38, 2, 70, 3)
        });
        root.Children.Add(new Rectangle
        {
            Stroke = ink, StrokeThickness = 1.5, RadiusX = 4, RadiusY = 4, Margin = new Thickness(7),
            StrokeDashArray = Dash(1.5, 52, 2, 34, 2, 66, 3)
        });

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (def.Icon != null) content.Children.Add(BuildIcon(def.Icon, ink));
        content.Children.Add(new TextBlock
        {
            Text = def.Text,
            Foreground = ink,
            FontSize = def.FontSize,
            FontFamily = HeavyFont,
            FontWeight = FontWeights.Black,
            VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(content);
        return root;
    }

    private static FrameworkElement BuildIcon(string icon, Brush ink)
    {
        var c = new Canvas { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        switch (icon)
        {
            case "thumb":
                c.Width = 26; c.Height = 26;
                c.Children.Add(new Path { Fill = ink, Data = Geometry.Parse("M0 14 H5 V25 H0 Z") });
                c.Children.Add(new Path
                {
                    Fill = ink,
                    Data = Geometry.Parse("M7 25 h11 q3 0 3.6 -3 l1.6 -8 q0.6 -3 -2.6 -3 h-6.2 l1.1 -5.2 q0.6 -3 -2.6 -3.6 l-4.9 8.8 v14 z")
                });
                break;
            case "check":
                c.Width = 21; c.Height = 20;
                c.Children.Add(new Path
                {
                    Stroke = ink, StrokeThickness = 5,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Data = Geometry.Parse("M0 10 L7 19 L20 1")
                });
                break;
            case "bug":
                c.Width = 30; c.Height = 29;
                c.Children.Add(new Path
                {
                    Stroke = ink, StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Data = Geometry.Parse("M8 13 L1 9 M8 19 L0 19 M8 25 L1 29 M22 13 L29 9 M22 19 L30 19 M22 25 L29 29 M12 5 L9 0 M18 5 L21 0")
                });
                var head = new Ellipse { Width = 8, Height = 8, Fill = ink };
                Canvas.SetLeft(head, 11); Canvas.SetTop(head, 4);
                c.Children.Add(head);
                var body = new Ellipse { Width = 14, Height = 18, Fill = ink };
                Canvas.SetLeft(body, 8); Canvas.SetTop(body, 11);
                c.Children.Add(body);
                break;
        }
        return c;
    }

    private static FrameworkElement BuildCircle(StampDef def, Brush ink)
    {
        var root = new Grid
        {
            Width = def.W, Height = def.H,
            Background = Brushes.Transparent, OpacityMask = GrungeMask
        };
        var cv = new Canvas { Width = def.W, Height = def.H };

        var outer = new Ellipse
        {
            Width = 116, Height = 116, Stroke = ink, StrokeThickness = 3.5,
            StrokeDashArray = Dash(3.5, 60, 3, 74, 2, 52, 3, 48, 2)
        };
        Canvas.SetLeft(outer, 7); Canvas.SetTop(outer, 7);
        cv.Children.Add(outer);

        var inner = new Ellipse
        {
            Width = 98, Height = 98, Stroke = ink, StrokeThickness = 1.5,
            StrokeDashArray = Dash(1.5, 55, 2, 68, 3, 40, 2)
        };
        Canvas.SetLeft(inner, 16); Canvas.SetTop(inner, 16);
        cv.Children.Add(inner);

        foreach (var y in new[] { 54.0, 76.0 })
        {
            cv.Children.Add(new Line
            {
                X1 = 27, Y1 = y, X2 = 103, Y2 = y,
                Stroke = ink, StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }

        root.Children.Add(cv);
        root.Children.Add(new TextBlock
        {
            Text = def.Text,
            Foreground = ink,
            FontSize = def.FontSize,
            FontFamily = HeavyFont,
            FontWeight = FontWeights.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return root;
    }
}

/// <summary>
/// A placed stamp. The visual keeps its natural size; scale and rotation live in a
/// center-origin TransformGroup on the inner element, so everything stays vector-sharp
/// and bakes into the export automatically. The outer container carries no transform
/// of its own — the Move tool is free to attach its TranslateTransform there without
/// clobbering the stamp's rotation.
/// </summary>
public sealed class StampAnnotation : Annotation, IEditTarget
{
    private readonly Grid _outer;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly RotateTransform _rotate = new(0);

    public StampDef Def { get; }
    public override UIElement Element => _outer;

    /// <summary>Whether this stamp re-picks deep/bright ink from the background it sits on.</summary>
    public bool AutoInk { get; init; }
    public bool BrightInk { get; private set; }

    public StampAnnotation(StampDef def, bool brightInk = false)
    {
        Def = def;
        BrightInk = brightInk;
        _outer = new Grid { Width = def.W, Height = def.H };
        _outer.Children.Add(BuildInner(brightInk));
    }

    /// <summary>The transforms are shared instances, so a rebuilt visual keeps the pose.</summary>
    private FrameworkElement BuildInner(bool brightInk)
    {
        var visual = StampCatalog.BuildVisual(Def, brightInk);
        visual.RenderTransformOrigin = new Point(0.5, 0.5);
        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_rotate);
        visual.RenderTransform = group;
        return visual;
    }

    /// <summary>Swaps the ink in place (Auto mode) without touching position or pose.</summary>
    public void SetBrightInk(bool brightInk)
    {
        if (brightInk == BrightInk) return;
        BrightInk = brightInk;
        _outer.Children.Clear();
        _outer.Children.Add(BuildInner(brightInk));
    }

    public double Angle
    {
        get => _rotate.Angle;
        set => _rotate.Angle = value;
    }

    public double Scale
    {
        get => _scale.ScaleX;
        set { _scale.ScaleX = value; _scale.ScaleY = value; }
    }

    private Vector Translation =>
        _outer.RenderTransform is TranslateTransform t ? new Vector(t.X, t.Y) : default;

    /// <summary>Stamp center in canvas coordinates, including any Move-tool translation.</summary>
    public Point Center
    {
        get
        {
            var tr = Translation;
            return new Point(Canvas.GetLeft(_outer) + Def.W / 2 + tr.X,
                             Canvas.GetTop(_outer) + Def.H / 2 + tr.Y);
        }
        set
        {
            var tr = Translation;
            Canvas.SetLeft(_outer, value.X - Def.W / 2 - tr.X);
            Canvas.SetTop(_outer, value.Y - Def.H / 2 - tr.Y);
        }
    }

    /// <summary>Half-extents of the scaled (unrotated) stamp.</summary>
    public Vector HalfSize => new(Def.W / 2 * Scale, Def.H / 2 * Scale);

    /// <summary>Half of the natural diagonal — the corner-to-center distance at scale 1.</summary>
    public double NaturalHalfDiag => Math.Sqrt(Def.W * Def.W + Def.H * Def.H) / 2;

    /// <summary>Point-in-rotated-rect test in canvas coordinates.</summary>
    public bool Contains(Point p)
    {
        var v = p - Center;
        double a = -Angle * Math.PI / 180;
        double lx = v.X * Math.Cos(a) - v.Y * Math.Sin(a);
        double ly = v.X * Math.Sin(a) + v.Y * Math.Cos(a);
        var h = HalfSize;
        return Math.Abs(lx) <= h.X && Math.Abs(ly) <= h.Y;
    }
}

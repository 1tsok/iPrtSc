using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace iPrtSc;

/// <summary>Base for a single vector annotation rendered as a WPF element.</summary>
public abstract class Annotation
{
    public abstract UIElement Element { get; }
}

/// <summary>Annotations defined by a drag from point A to point B.</summary>
public interface IDragAnnotation
{
    void Update(Point a, Point b);
}

/// <summary>Freehand annotations that accumulate points.</summary>
public interface IFreehandAnnotation
{
    void Add(Point p);
    void SetLine(Point a, Point b);
}

/// <summary>
/// Freehand stroke rendered as a smooth Bézier path. Raw mouse points are first
/// thinned (samples closer than <see cref="MinDistance"/> are dropped to kill jitter),
/// then connected with a Catmull-Rom spline converted to cubic Béziers so the curve
/// passes through the kept points but stays smooth. The whole geometry is rebuilt on
/// each Add — fine for a single stroke (hundreds of points at most).
/// </summary>
public abstract class FreehandStrokeAnnotation : Annotation, IFreehandAnnotation
{
    /// <summary>Minimum spacing (px) between kept points; smaller samples are dropped.</summary>
    private const double MinDistance = 4.0;

    /// <summary>Moving-average passes applied to the points before curve fitting.
    /// Higher = smoother but lags the cursor and rounds off sharp corners.</summary>
    private const int SmoothingPasses = 2;

    private readonly Path _path = new();
    private readonly List<Point> _pts = new();
    public override UIElement Element => _path;

    protected FreehandStrokeAnnotation(Brush stroke, double thickness, double opacity = 1.0)
    {
        _path.Stroke = stroke;
        _path.StrokeThickness = thickness;
        _path.StrokeLineJoin = PenLineJoin.Round;
        _path.StrokeStartLineCap = PenLineCap.Round;
        _path.StrokeEndLineCap = PenLineCap.Round;
        _path.Opacity = opacity;
    }

    public void Add(Point p)
    {
        if (_pts.Count > 0)
        {
            var last = _pts[^1];
            double dx = p.X - last.X, dy = p.Y - last.Y;
            if (dx * dx + dy * dy < MinDistance * MinDistance)
                return;
        }
        _pts.Add(p);
        _path.Data = BuildGeometry(Smooth(_pts));
    }

    /// <summary>
    /// Shift mode: replaces the stroke with a straight segment from <paramref name="a"/>
    /// to <paramref name="b"/>. Releasing Shift lets <see cref="Add"/> continue freehand
    /// from the segment's end.
    /// </summary>
    public void SetLine(Point a, Point b)
    {
        _pts.Clear();
        _pts.Add(a);
        _pts.Add(b);
        _path.Data = BuildGeometry(_pts);
    }

    /// <summary>
    /// Low-pass filter: replaces each interior point with a weighted average of its
    /// neighbours (endpoints stay pinned so the stroke starts/ends where drawn).
    /// Applied a few times to tame jitter before the curve is fitted.
    /// </summary>
    private static IReadOnlyList<Point> Smooth(IReadOnlyList<Point> src)
    {
        if (src.Count < 3 || SmoothingPasses <= 0)
            return src;

        var cur = new Point[src.Count];
        for (int i = 0; i < src.Count; i++) cur[i] = src[i];

        var next = new Point[src.Count];
        for (int pass = 0; pass < SmoothingPasses; pass++)
        {
            next[0] = cur[0];
            next[^1] = cur[^1];
            for (int i = 1; i < cur.Length - 1; i++)
            {
                next[i] = new Point(
                    0.25 * cur[i - 1].X + 0.5 * cur[i].X + 0.25 * cur[i + 1].X,
                    0.25 * cur[i - 1].Y + 0.5 * cur[i].Y + 0.25 * cur[i + 1].Y);
            }
            (cur, next) = (next, cur);
        }
        return cur;
    }

    /// <summary>Catmull-Rom through the points, emitted as cubic Bézier segments.</summary>
    private static Geometry BuildGeometry(IReadOnlyList<Point> pts)
    {
        var fig = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };

        if (pts.Count == 1)
        {
            // A dot: zero-length line so the round caps render a filled circle.
            fig.Segments.Add(new LineSegment(pts[0], true));
        }
        else if (pts.Count == 2)
        {
            fig.Segments.Add(new LineSegment(pts[1], true));
        }
        else
        {
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var p0 = pts[Math.Max(i - 1, 0)];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = pts[Math.Min(i + 2, pts.Count - 1)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
            }
        }

        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }
}

public sealed class FreehandAnnotation : FreehandStrokeAnnotation
{
    public FreehandAnnotation(Brush stroke, double thickness)
        : base(stroke, thickness) { }
}

/// <summary>
/// Freehand highlighter (marker): wide and semi-transparent so it tints the area
/// rather than covering it. Opacity lives on the element, not the brush, so a single
/// stroke composites once and overlaps within it don't darken.
/// </summary>
public sealed class HighlighterAnnotation : FreehandStrokeAnnotation
{
    public HighlighterAnnotation(Brush stroke, double thickness)
        : base(stroke, thickness, opacity: 0.4) { }
}

public sealed class LineAnnotation : Annotation, IDragAnnotation
{
    private readonly Line _line;
    public override UIElement Element => _line;

    public LineAnnotation(Brush stroke, double thickness)
    {
        _line = new Line
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
    }

    public void Update(Point a, Point b)
    {
        _line.X1 = a.X; _line.Y1 = a.Y;
        _line.X2 = b.X; _line.Y2 = b.Y;
    }
}

public sealed class ArrowAnnotation : Annotation, IDragAnnotation
{
    private readonly Path _path;
    private readonly double _thickness;
    public override UIElement Element => _path;

    public ArrowAnnotation(Brush stroke, double thickness)
    {
        _thickness = thickness;
        _path = new Path
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
    }

    public void Update(Point a, Point b)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(a, false, false);
            c.LineTo(b, true, true);

            double angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
            double head = Math.Max(12, _thickness * 4);
            const double spread = Math.PI / 7;

            var p1 = new Point(b.X - head * Math.Cos(angle - spread), b.Y - head * Math.Sin(angle - spread));
            var p2 = new Point(b.X - head * Math.Cos(angle + spread), b.Y - head * Math.Sin(angle + spread));

            c.BeginFigure(p1, false, false);
            c.LineTo(b, true, true);
            c.LineTo(p2, true, true);
        }
        g.Freeze();
        _path.Data = g;
    }
}

public sealed class RectAnnotation : Annotation, IDragAnnotation
{
    private readonly Rectangle _rect;
    public override UIElement Element => _rect;

    public RectAnnotation(Brush stroke, double thickness)
    {
        _rect = new Rectangle
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            Fill = Brushes.Transparent
        };
    }

    public void Update(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        Canvas.SetLeft(_rect, x);
        Canvas.SetTop(_rect, y);
        _rect.Width = Math.Abs(a.X - b.X);
        _rect.Height = Math.Abs(a.Y - b.Y);
    }
}

public sealed class EllipseAnnotation : Annotation, IDragAnnotation
{
    private readonly Ellipse _ellipse;
    public override UIElement Element => _ellipse;

    public EllipseAnnotation(Brush stroke, double thickness)
    {
        _ellipse = new Ellipse
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            Fill = Brushes.Transparent
        };
    }

    public void Update(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        Canvas.SetLeft(_ellipse, x);
        Canvas.SetTop(_ellipse, y);
        _ellipse.Width = Math.Abs(a.X - b.X);
        _ellipse.Height = Math.Abs(a.Y - b.Y);
    }
}

/// <summary>
/// Pixelates (mosaics) a rectangular area sampled from the full screenshot.
/// Implements <see cref="IEditTarget"/> natively rather than going through
/// <see cref="ElementEditTarget"/>: a render transform would carry the frozen
/// mosaic along, whereas moving or scaling this annotation re-crops the source
/// at the new rectangle, so the mosaic always shows what is underneath it.
/// The crop is axis-aligned, so rotation is not supported.
/// </summary>
public sealed class PixelateAnnotation : Annotation, IDragAnnotation, IEditTarget
{
    private readonly Image _img;
    private readonly BitmapSource _source;
    private readonly double _scale;
    private readonly int _block;

    private Rect _area;          // drawn rectangle, canvas coordinates
    private Size _natural;       // size at scale 1 (the size it was drawn at)
    private double _factor = 1;

    public override UIElement Element => _img;

    public PixelateAnnotation(BitmapSource source, double scale, int block = 10)
    {
        _source = source;
        _scale = scale;
        _block = Math.Max(2, block);
        _img = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.NearestNeighbor);
    }

    public void Update(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(a.X - b.X), h = Math.Abs(a.Y - b.Y);
        _natural = new Size(w, h);
        _factor = 1;
        SetArea(new Rect(x, y, w, h));
    }

    private void SetArea(Rect r)
    {
        _area = r;
        Resample();
    }

    /// <summary>
    /// Re-crops the source under <see cref="_area"/>. The crop is expanded to whole
    /// mosaic cells so the cell grid stays anchored to the screenshot instead of
    /// swimming while the object is dragged; the element is then clipped back to the
    /// drawn rectangle, which is what the user sees and what hit-testing reports.
    /// </summary>
    private void Resample()
    {
        Canvas.SetLeft(_img, _area.X);
        Canvas.SetTop(_img, _area.Y);
        _img.Width = _area.Width;
        _img.Height = _area.Height;
        _img.Clip = null;

        if (_area.Width < 1 || _area.Height < 1) { _img.Source = null; return; }

        int Align(double v, int dir) =>
            dir < 0 ? (int)Math.Floor(v / _block) * _block : (int)Math.Ceiling(v / _block) * _block;

        int ax = Math.Clamp(Align(_area.X * _scale, -1), 0, _source.PixelWidth - 1);
        int ay = Math.Clamp(Align(_area.Y * _scale, -1), 0, _source.PixelHeight - 1);
        int ar = Math.Clamp(Align(_area.Right * _scale, 1), ax + 1, _source.PixelWidth);
        int ab = Math.Clamp(Align(_area.Bottom * _scale, 1), ay + 1, _source.PixelHeight);
        int aw = ar - ax, ah = ab - ay;
        if (aw < 2 || ah < 2) { _img.Source = null; return; }

        var crop = new CroppedBitmap(_source, new Int32Rect(ax, ay, aw, ah));
        int dw = Math.Max(1, aw / _block);
        int dh = Math.Max(1, ah / _block);
        var small = new TransformedBitmap(crop, new ScaleTransform((double)dw / aw, (double)dh / ah));
        small.Freeze();
        _img.Source = small;

        double ex = ax / _scale, ey = ay / _scale;
        Canvas.SetLeft(_img, ex);
        Canvas.SetTop(_img, ey);
        _img.Width = aw / _scale;
        _img.Height = ah / _scale;
        _img.Clip = new RectangleGeometry(
            new Rect(_area.X - ex, _area.Y - ey, _area.Width, _area.Height));
    }

    // ===== IEditTarget =====
    public Point Center
    {
        get => new(_area.X + _area.Width / 2, _area.Y + _area.Height / 2);
        set => SetArea(new Rect(
            value.X - _area.Width / 2, value.Y - _area.Height / 2, _area.Width, _area.Height));
    }

    public double Scale
    {
        get => _factor;
        set
        {
            var c = Center;
            _factor = value;
            double w = _natural.Width * _factor, h = _natural.Height * _factor;
            SetArea(new Rect(c.X - w / 2, c.Y - h / 2, w, h));
        }
    }

    /// <summary>Always 0: the mosaic is sampled on an axis-aligned grid.</summary>
    public double Angle
    {
        get => 0;
        set { /* rotation would break resampling */ }
    }

    public Vector HalfSize => new(_area.Width / 2, _area.Height / 2);

    public double NaturalHalfDiag =>
        Math.Sqrt(_natural.Width * _natural.Width + _natural.Height * _natural.Height) / 2;
}

public sealed class TextAnnotation : Annotation
{
    private readonly TextBox _box;
    public override UIElement Element => _box;
    public TextBox Box => _box;

    public TextAnnotation(Brush foreground, double fontSize)
    {
        _box = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = foreground,
            CaretBrush = foreground,
            FontSize = fontSize,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            AcceptsReturn = true,
            MinWidth = 24,
            Padding = new Thickness(0)
        };
    }
}

public sealed class CounterAnnotation : Annotation
{
    private readonly Grid _grid;
    public override UIElement Element => _grid;

    public CounterAnnotation(Brush fill, int number, double diameter)
    {
        _grid = new Grid { Width = diameter, Height = diameter };
        _grid.Children.Add(new Ellipse
        {
            Fill = fill,
            Stroke = Brushes.White,
            StrokeThickness = 2
        });
        _grid.Children.Add(new TextBlock
        {
            Text = number.ToString(),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = diameter * 0.52,
            FontFamily = new FontFamily("Segoe UI"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
    }
}

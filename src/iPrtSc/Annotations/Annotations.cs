using System;
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
}

public sealed class FreehandAnnotation : Annotation, IFreehandAnnotation
{
    private readonly Polyline _line;
    public override UIElement Element => _line;

    public FreehandAnnotation(Brush stroke, double thickness)
    {
        _line = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
    }

    public void Add(Point p) => _line.Points.Add(p);
}

/// <summary>
/// Freehand highlighter (marker): wide and semi-transparent so it tints the area
/// rather than covering it. Opacity lives on the element, not the brush, so a single
/// stroke composites once and overlaps within it don't darken.
/// </summary>
public sealed class HighlighterAnnotation : Annotation, IFreehandAnnotation
{
    private readonly Polyline _line;
    public override UIElement Element => _line;

    public HighlighterAnnotation(Brush stroke, double thickness)
    {
        _line = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0.4
        };
    }

    public void Add(Point p) => _line.Points.Add(p);
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

/// <summary>Pixelates (mosaics) a rectangular area sampled from the full screenshot.</summary>
public sealed class PixelateAnnotation : Annotation, IDragAnnotation
{
    private readonly Image _img;
    private readonly BitmapSource _source;
    private readonly double _scale;
    private readonly int _block;

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
        Canvas.SetLeft(_img, x);
        Canvas.SetTop(_img, y);
        _img.Width = w;
        _img.Height = h;

        int px = (int)Math.Round(x * _scale);
        int py = (int)Math.Round(y * _scale);
        int pw = (int)Math.Round(w * _scale);
        int ph = (int)Math.Round(h * _scale);
        px = Math.Clamp(px, 0, _source.PixelWidth - 1);
        py = Math.Clamp(py, 0, _source.PixelHeight - 1);
        pw = Math.Clamp(pw, 1, _source.PixelWidth - px);
        ph = Math.Clamp(ph, 1, _source.PixelHeight - py);
        if (pw < 2 || ph < 2) { _img.Source = null; return; }

        var crop = new CroppedBitmap(_source, new Int32Rect(px, py, pw, ph));
        int dw = Math.Max(1, pw / _block);
        int dh = Math.Max(1, ph / _block);
        var small = new TransformedBitmap(crop, new ScaleTransform((double)dw / pw, (double)dh / ph));
        small.Freeze();
        _img.Source = small;
    }
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

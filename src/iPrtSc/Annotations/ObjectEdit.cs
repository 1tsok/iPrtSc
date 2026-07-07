using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace iPrtSc;

/// <summary>
/// An object on the annotation canvas that can be moved, uniformly scaled and rotated
/// through the shared edit handles (corner grips + rotate grip). Implemented natively
/// by <see cref="StampAnnotation"/> and retrofitted onto any other annotation element
/// by <see cref="ElementEditTarget"/>.
/// </summary>
public interface IEditTarget
{
    /// <summary>Object center in canvas coordinates.</summary>
    Point Center { get; set; }

    /// <summary>Uniform scale factor applied around the center.</summary>
    double Scale { get; set; }

    /// <summary>Rotation in degrees around the center.</summary>
    double Angle { get; set; }

    /// <summary>Half-extents of the scaled (unrotated) bounds.</summary>
    Vector HalfSize { get; }

    /// <summary>Half of the natural diagonal — corner-to-center distance at scale 1.</summary>
    double NaturalHalfDiag { get; }

    /// <summary>Point-in-rotated-rect test in canvas coordinates.</summary>
    bool Contains(Point p)
    {
        var v = p - Center;
        double a = -Angle * Math.PI / 180;
        double lx = v.X * Math.Cos(a) - v.Y * Math.Sin(a);
        double ly = v.X * Math.Sin(a) + v.Y * Math.Cos(a);
        var h = HalfSize;
        return Math.Abs(lx) <= h.X && Math.Abs(ly) <= h.Y;
    }
}

/// <summary>
/// Wraps an arbitrary annotation element so the Move tool can edit it like a stamp.
/// A center-pivot [Scale, Rotate, Translate] TransformGroup is installed on the element
/// (preserving any TranslateTransform left by earlier versions of the Move tool); the
/// pivot is the center of the element's untransformed bounds, so scale and rotation
/// spin the object in place. The group is reused when the same element is picked again,
/// keeping its accumulated scale, angle and offset.
/// </summary>
public sealed class ElementEditTarget : IEditTarget
{
    private readonly UIElement _el;
    private readonly Rect _bounds;                 // untransformed bounds, element space
    private readonly ScaleTransform _scale;
    private readonly RotateTransform _rotate;
    private readonly TranslateTransform _translate;

    public UIElement Element => _el;

    public ElementEditTarget(UIElement el)
    {
        _el = el;
        var b = VisualTreeHelper.GetDescendantBounds(el);
        if (b.IsEmpty) b = new Rect(el.RenderSize);
        _bounds = b;

        if (el.RenderTransform is TransformGroup g && g.Children.Count == 3
            && g.Children[0] is ScaleTransform s
            && g.Children[1] is RotateTransform r
            && g.Children[2] is TranslateTransform t)
        {
            _scale = s; _rotate = r; _translate = t;
        }
        else
        {
            double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
            _scale = new ScaleTransform(1, 1, cx, cy);
            _rotate = new RotateTransform(0, cx, cy);
            _translate = el.RenderTransform is TranslateTransform old
                ? new TranslateTransform(old.X, old.Y)
                : new TranslateTransform();
            var ng = new TransformGroup();
            ng.Children.Add(_scale);
            ng.Children.Add(_rotate);
            ng.Children.Add(_translate);
            el.RenderTransform = ng;
        }
    }

    /// <summary>The transform pivot in element space (bounds center at install time).</summary>
    private Point Pivot => new(_scale.CenterX, _scale.CenterY);

    private Vector CanvasOffset()
    {
        double x = Canvas.GetLeft(_el); if (double.IsNaN(x)) x = 0;
        double y = Canvas.GetTop(_el); if (double.IsNaN(y)) y = 0;
        return new Vector(x, y);
    }

    public Point Center
    {
        get
        {
            var o = CanvasOffset();
            return new Point(o.X + Pivot.X + _translate.X, o.Y + Pivot.Y + _translate.Y);
        }
        set
        {
            var o = CanvasOffset();
            _translate.X = value.X - o.X - Pivot.X;
            _translate.Y = value.Y - o.Y - Pivot.Y;
        }
    }

    public double Scale
    {
        get => _scale.ScaleX;
        set { _scale.ScaleX = value; _scale.ScaleY = value; }
    }

    public double Angle
    {
        get => _rotate.Angle;
        set => _rotate.Angle = value;
    }

    public Vector HalfSize => new(_bounds.Width / 2 * Scale, _bounds.Height / 2 * Scale);

    public double NaturalHalfDiag =>
        Math.Sqrt(_bounds.Width * _bounds.Width + _bounds.Height * _bounds.Height) / 2;
}

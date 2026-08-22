using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace iPrtSc;

/// <summary>The shared move/scale/rotate machinery for whatever object is selected.</summary>
public partial class OverlayWindow
{
    // Object editing (Move tool + Stamp tool): the object currently showing the
    // move/scale/rotate handles, and its state at gesture start for undo.
    private IEditTarget? _editTarget;
    private (Point C, double S, double A) _gestureOrig;

    private StampAnnotation? _editStamp;                   // stamp currently showing handles
    private readonly List<System.Windows.Shapes.Ellipse> _stampCorners = new();
    private Grid? _stampRotHandle;
    private bool _stampScaling, _stampRotating, _stampMoving;
    private double _stampRotOffset;    // stamp angle minus pointer angle at grab
    private Vector _stampMoveOffset;   // pointer minus stamp center at grab

    private void ActivateStampEdit(StampAnnotation stamp) => SetEditTarget(stamp);

    private void DeactivateStampEdit() => SetEditTarget(null);

    /// <summary>Makes an object the active edit target (or clears it) and syncs the handles.</summary>
    private void SetEditTarget(IEditTarget? target)
    {
        _editTarget = target;
        _editStamp = target as StampAnnotation;
        PositionStampHandles();
    }

    private static UIElement? TargetElement(IEditTarget t) => t switch
    {
        StampAnnotation sa => sa.Element,
        PixelateAnnotation pa => pa.Element,
        ElementEditTarget ee => ee.Element,
        _ => null
    };

    /// <summary>Del/Backspace: removes the selected object from the canvas (undoable).</summary>
    private void DeleteEditTarget()
    {
        var target = _editTarget;
        if (target == null || TargetElement(target) is not UIElement el) return;

        var stamp = target as StampAnnotation;
        var pixelate = target as PixelateAnnotation;
        void Remove()
        {
            AnnotCanvas.Children.Remove(el);
            if (stamp != null) _stamps.Remove(stamp);
            if (pixelate != null) _pixelates.Remove(pixelate);
            if (_editTarget != null && ReferenceEquals(TargetElement(_editTarget), el))
                SetEditTarget(null);
        }
        Remove();
        PushUndoItem(
            undo: () =>
            {
                AnnotCanvas.Children.Add(el);
                if (stamp != null) _stamps.Add(stamp);
                if (pixelate != null) _pixelates.Add(pixelate);
            },
            redo: Remove);
    }

    /// <summary>Snapshots the target's placement so the gesture can be undone as one step.</summary>
    private void BeginGesture()
    {
        var t = _editTarget;
        if (t != null) _gestureOrig = (t.Center, t.Scale, t.Angle);
    }

    private void EndGesture()
    {
        var t = _editTarget;
        if (t == null) return;
        var (c0, s0, a0) = _gestureOrig;
        var (c1, s1, a1) = (t.Center, t.Scale, t.Angle);
        if (c0 == c1 && s0 == s1 && a0 == a1) return;
        PushUndoItem(
            undo: () => { t.Center = c0; t.Scale = s0; t.Angle = a0; PositionStampHandles(); },
            redo: () => { t.Center = c1; t.Scale = s1; t.Angle = a1; PositionStampHandles(); });
    }

    private void EnsureStampHandles()
    {
        if (_stampCorners.Count > 0) return;
        var accent = SelBorder.Stroke;

        for (int i = 0; i < 4; i++)
        {
            var h = new System.Windows.Shapes.Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = Brushes.White,
                Stroke = accent,
                StrokeThickness = 1.5,
                Visibility = Visibility.Collapsed,
                Cursor = i % 2 == 0 ? Cursors.SizeNWSE : Cursors.SizeNESW
            };
            h.MouseLeftButtonDown += OnStampCornerDown;
            h.MouseMove += OnStampCornerMove;
            h.MouseLeftButtonUp += OnStampCornerUp;
            HandleCanvas.Children.Add(h);
            _stampCorners.Add(h);
        }

        _stampRotHandle = new Grid
        {
            Width = 30,
            Height = 30,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent
        };
        var rotIdle = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
        var rotHover = new SolidColorBrush(Color.FromRgb(0x47, 0x47, 0x47));
        var rotBg = new System.Windows.Shapes.Ellipse { Fill = rotIdle };
        _stampRotHandle.Children.Add(rotBg);
        _stampRotHandle.MouseEnter += (_, _) => rotBg.Fill = rotHover;
        _stampRotHandle.MouseLeave += (_, _) => rotBg.Fill = rotIdle;
        _stampRotHandle.Children.Add(new System.Windows.Shapes.Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse("M3 12a9 9 0 1 0 9-9 9 9 0 0 0-6.4 2.6L3 8 M3 3v5h5")
        });
        _stampRotHandle.MouseLeftButtonDown += OnStampRotateDown;
        _stampRotHandle.MouseMove += OnStampRotateMove;
        _stampRotHandle.MouseLeftButtonUp += OnStampRotateUp;
        HandleCanvas.Children.Add(_stampRotHandle);
    }

    /// <summary>
    /// Places the four corner grips on the stamp's rotated corners and the rotate grip
    /// above its top edge. Handles live on HandleCanvas (over the mouse surface), so
    /// they receive mouse input directly and are never part of the exported image.
    /// </summary>
    private void PositionStampHandles()
    {
        // Every move/scale/rotate/undo path funnels through here — the one spot where
        // an Auto stamp re-checks the background it now sits on.
        if (_editTarget is StampAnnotation sa) ResampleAutoInk(sa);

        bool show = _editTarget != null && _tool is Tool.Stamp or Tool.Move;
        if (!show)
        {
            foreach (var h in _stampCorners) h.Visibility = Visibility.Collapsed;
            if (_stampRotHandle != null) _stampRotHandle.Visibility = Visibility.Collapsed;
            return;
        }

        EnsureStampHandles();
        var c = _editTarget!.Center;
        var half = _editTarget.HalfSize;
        double a = _editTarget.Angle * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        Point At(double lx, double ly) => new(c.X + lx * cos - ly * sin, c.Y + lx * sin + ly * cos);

        Point[] corners = { At(-half.X, -half.Y), At(half.X, -half.Y), At(half.X, half.Y), At(-half.X, half.Y) };
        for (int i = 0; i < 4; i++)
        {
            Canvas.SetLeft(_stampCorners[i], corners[i].X - 6);
            Canvas.SetTop(_stampCorners[i], corners[i].Y - 6);
            _stampCorners[i].Visibility = Visibility.Visible;
        }

        // Pixelate samples on an axis-aligned grid, so it gets no rotate grip.
        if (_editTarget is PixelateAnnotation)
        {
            _stampRotHandle!.Visibility = Visibility.Collapsed;
            return;
        }

        var rp = At(0, -half.Y - 30);
        Canvas.SetLeft(_stampRotHandle!, rp.X - 15);
        Canvas.SetTop(_stampRotHandle!, rp.Y - 15);
        _stampRotHandle!.Visibility = Visibility.Visible;
    }

    private void OnStampCornerDown(object sender, MouseButtonEventArgs e)
    {
        if (_editTarget == null) return;
        BeginGesture();
        _stampScaling = true;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnStampCornerMove(object sender, MouseEventArgs e)
    {
        if (!_stampScaling || _editTarget == null) return;
        // Uniform scale from the center: the grabbed corner follows the pointer's distance.
        double dist = (e.GetPosition(Root) - _editTarget.Center).Length;
        _editTarget.Scale = Math.Clamp(dist / _editTarget.NaturalHalfDiag, 0.3, 5);
        PositionStampHandles();
        e.Handled = true;
    }

    private void OnStampCornerUp(object sender, MouseButtonEventArgs e)
    {
        _stampScaling = false;
        ((UIElement)sender).ReleaseMouseCapture();
        EndGesture();
        e.Handled = true;
    }

    private void OnStampRotateDown(object sender, MouseButtonEventArgs e)
    {
        if (_editTarget == null) return;
        BeginGesture();
        _stampRotating = true;
        _stampRotOffset = _editTarget.Angle - PointerAngle(e.GetPosition(Root));
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnStampRotateMove(object sender, MouseEventArgs e)
    {
        if (!_stampRotating || _editTarget == null) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        _editTarget.Angle = SnapAngle(PointerAngle(e.GetPosition(Root)) + _stampRotOffset, shift);
        PositionStampHandles();
        e.Handled = true;
    }

    private void OnStampRotateUp(object sender, MouseButtonEventArgs e)
    {
        _stampRotating = false;
        ((UIElement)sender).ReleaseMouseCapture();
        EndGesture();
        e.Handled = true;
    }

    private double PointerAngle(Point p)
    {
        var v = p - _editTarget!.Center;
        return Math.Atan2(v.Y, v.X) * 180 / Math.PI;
    }

    /// <summary>Free rotation with magnets on the axes; Shift steps by 15°.</summary>
    private static double SnapAngle(double deg, bool shiftStep)
    {
        deg %= 360;
        if (deg > 180) deg -= 360;
        if (deg < -180) deg += 360;
        if (shiftStep) return Math.Round(deg / 15) * 15;
        foreach (double target in new[] { 0.0, 90, 180, -180, -90 })
            if (Math.Abs(deg - target) <= 4)
                return target == -180 ? 180 : target;
        return deg;
    }

    // ===== Move tool =====
    /// <summary>
    /// Click selects the annotation under the pointer (stamps keep their own richer
    /// target; anything else is wrapped in an <see cref="ElementEditTarget"/>) and
    /// starts a move drag. Clicking inside the already-active object's rotated bounds
    /// re-grabs it even where its strokes are thin; clicking empty space deselects.
    /// </summary>
    /// <summary>True when the Move tool has something to grab under the pointer.</summary>
    private bool MovableAt(Point p)
    {
        if (_editTarget?.Contains(p) == true) return true;
        var result = VisualTreeHelper.HitTest(AnnotCanvas, p);
        return result?.VisualHit is DependencyObject hit && TopLevelChild(hit) is UIElement;
    }

    private void TryBeginMove(Point p)
    {
        var target = _editTarget?.Contains(p) == true ? _editTarget : null;

        if (target == null)
        {
            var result = VisualTreeHelper.HitTest(AnnotCanvas, p);
            if (result?.VisualHit is DependencyObject hit && TopLevelChild(hit) is UIElement el)
            {
                target = (IEditTarget?)_stamps.FirstOrDefault(s => ReferenceEquals(s.Element, el))
                    ?? (IEditTarget?)_pixelates.FirstOrDefault(px => ReferenceEquals(px.Element, el))
                    ?? (_editTarget is ElementEditTarget ee && ReferenceEquals(ee.Element, el)
                        ? ee
                        : new ElementEditTarget(el));
            }
        }

        SetEditTarget(target);
        if (target == null) return;

        BeginGesture();
        _stampMoving = true;
        _stampMoveOffset = p - target.Center;
        Hit.CaptureMouse();
    }

    private DependencyObject? TopLevelChild(DependencyObject d)
    {
        var node = d;
        while (node != null)
        {
            var parent = VisualTreeHelper.GetParent(node);
            if (ReferenceEquals(parent, AnnotCanvas)) return node;
            node = parent;
        }
        return null;
    }
}

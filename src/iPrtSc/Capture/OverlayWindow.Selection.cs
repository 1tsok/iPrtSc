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
using WpfRect = System.Windows.Shapes.Rectangle;

namespace iPrtSc;

/// <summary>Selection visuals, the size label, panel placement and the resize handles.</summary>
public partial class OverlayWindow
{
    // Selection resize handles
    private readonly List<FrameworkElement> _handles = new();
    private bool _resizing;
    private string _activeHandle = "";
    private Vector _resizeGrab;   // dragged edge minus pointer, captured at grab

    /// <summary>Ctrl+A: select all words in the Grab-text tool, otherwise select the full screen.</summary>
    private void SelectAllOrFullScreen()
    {
        if (_tool == Tool.OcrText && _ocrWords.Count > 0)
        {
            _selWords.Clear();
            for (int i = 0; i < _ocrWords.Count; i++) _selWords.Add(i);
            UpdateOcrHighlights();
            return;
        }
        SelectFullScreen();
    }

    private void SelectFullScreen()
    {
        ClearAnnotations();
        _sel = new Rect(0, 0, Root.ActualWidth, Root.ActualHeight);
        UpdateSelection();
        ShowToolBar();
        ShowHandles();
    }

    private void DoClearSelection()
    {
        _sel = Rect.Empty;
        ClearAnnotations();
        ClearSelection();
    }

    // ===== Selection visuals =====
    private void UpdateSelection()
    {
        UpdateDim(_sel);
        _annotClip.Rect = _sel;

        Canvas.SetLeft(SelBorder, _sel.X);
        Canvas.SetTop(SelBorder, _sel.Y);
        SelBorder.Width = _sel.Width;
        SelBorder.Height = _sel.Height;
        SelBorder.Visibility = Visibility.Visible;

        int pw = (int)Math.Round(_sel.Width * _scale);
        int ph = (int)Math.Round(_sel.Height * _scale);
        string size = $"{pw} × {ph}";
        SizeLabel.Visibility = Visibility.Visible;
        // Re-measuring costs a layout pass, and the label only changes when its text does.
        // (A layout pass while it was collapsed zeroes DesiredSize, so measure then too.)
        if (SizeText.Text != size || SizeLabel.DesiredSize.Height < 1)
        {
            SizeText.Text = size;
            SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        // Centered over the selection, mirroring the tools bar below it: size on top, tools
        // underneath, both on the same axis. Clamped so a region at a screen edge keeps it on
        // screen instead of pushing it into the corner.
        double lw = SizeLabel.DesiredSize.Width;
        double lx = Math.Max(8, Math.Min(_sel.X + (_sel.Width - lw) / 2, Root.ActualWidth - lw - 8));
        double ly = _sel.Y - SizeLabel.DesiredSize.Height - 6;
        if (ly < 4) ly = _sel.Y + 6;
        Canvas.SetLeft(SizeLabel, lx);
        Canvas.SetTop(SizeLabel, ly);

        PositionHandles();
    }

    private void ClearSelection()
    {
        SelBorder.Visibility = Visibility.Collapsed;
        SizeLabel.Visibility = Visibility.Collapsed;
        ToolPanel.Visibility = Visibility.Collapsed;
        HideFlyouts();
        foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
        UpdateDim(Rect.Empty);
    }

    /// <summary>Set when the tools bar had to flip above the selection; flyouts follow it.</summary>
    private bool _toolsAbove;

    private void ShowToolBar()
    {
        ToolPanel.Visibility = Visibility.Visible;
        PositionPanels();
    }

    private void PositionPanels()
    {
        // Must clear the resize handles, which stick out past the selection edge.
        const double gap = 10;

        // One bar for everything, centered under the selection.
        ToolPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = ToolPanel.DesiredSize.Width, th = ToolPanel.DesiredSize.Height;
        double tx = Math.Max(8, Math.Min(_sel.Left + _sel.Width / 2 - tw / 2, Root.ActualWidth - tw - 8));
        double ty = _sel.Bottom + gap;
        _toolsAbove = ty + th > Root.ActualHeight - 8;
        if (_toolsAbove) ty = _sel.Top - th - gap;                            // flip above
        ty = Math.Max(8, Math.Min(ty, Root.ActualHeight - th - 8));
        Canvas.SetLeft(ToolPanel, tx);
        Canvas.SetTop(ToolPanel, ty);
    }

    private void UpdateDim(Rect s)
    {
        if (s.Width < 1 || s.Height < 1)
        {
            ClearImage.Visibility = Visibility.Collapsed;
            return;
        }
        // Only the clip moves, so the repaint stays inside the selection instead of
        // covering the whole desktop.
        _clearClip.Rect = s;
        ClearImage.Visibility = Visibility.Visible;
    }

    private static void Place(WpfRect r, double x, double y, double w, double h)
    {
        r.Visibility = Visibility.Visible;
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        r.Width = Math.Max(0, w);
        r.Height = Math.Max(0, h);
    }

    private static void Hide(WpfRect r)
    {
        r.Width = 0; r.Height = 0;
        r.Visibility = Visibility.Collapsed;
    }

    // ===== Resize handles =====
    private static readonly string[] HandleTags = { "TL", "T", "TR", "R", "BR", "B", "BL", "L" };

    private void BuildHandles()
    {
        foreach (var tag in HandleTags)
        {
            // Every handle is the same white dot centered on its point of the frame.
            var dot = new WpfRect
            {
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            // The dot cannot carry the transparent grab stroke itself: WPF fits a
            // Rectangle's geometry inside its layout size minus the stroke width, so a
            // 9px dot under an 11px stroke would render nothing at all. The padded
            // wrapper does the same job — a transparent Background still hit-tests.
            var h = new Grid { Background = Brushes.Transparent };
            h.Children.Add(dot);
            h.Tag = tag;
            h.Visibility = Visibility.Collapsed;
            h.Cursor = CursorFor(tag);
            h.MouseLeftButtonDown += OnHandleDown;
            h.MouseMove += OnHandleMove;
            h.MouseLeftButtonUp += OnHandleUp;
            h.MouseEnter += OnHandleEnter;
            h.MouseLeave += OnHandleLeave;
            HandleCanvas.Children.Add(h);
            _handles.Add(h);
        }
    }

    /// <summary>Diameter of a handle dot at rest.</summary>
    private const double HandleDot = 9;
    /// <summary>What it grows to while the pointer is on it.</summary>
    private const double HandleDotHover = 15;
    /// <summary>
    /// Invisible grab margin around each dot, widening it by half this on every side:
    /// a padded transparent wrapper, whose Background still hit-tests. A 9px dot is
    /// otherwise fiddly to hit.
    /// </summary>
    private const double HandleHitPad = 11;
    /// <summary>
    /// Shortest edge that still fits a mid dot between the two corner ones: below this
    /// the grown dots would collide, so the edge handle drops out.
    /// </summary>
    private const double EdgeDotMin = 40;

    private FrameworkElement? _hoverHandle;

    private void OnHandleEnter(object sender, MouseEventArgs e)
    {
        _hoverHandle = (FrameworkElement)sender;
        PositionHandles();
    }

    private void OnHandleLeave(object sender, MouseEventArgs e)
    {
        // Keep the handle grown for the whole resize drag: the pointer leaves it as soon
        // as the frame follows the mouse, and flicking back to normal mid-drag looks broken.
        if (_resizing) return;
        if (ReferenceEquals(_hoverHandle, sender)) _hoverHandle = null;
        PositionHandles();
    }

    private static Cursor CursorFor(string tag) => tag switch
    {
        "TL" or "BR" => Cursors.SizeNWSE,
        "TR" or "BL" => Cursors.SizeNESW,
        "T" or "B" => Cursors.SizeNS,
        _ => Cursors.SizeWE
    };

    private void ShowHandles()
    {
        bool show = _tool == Tool.Select && !_sel.IsEmpty;
        if (show) PositionHandles();
        else foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
    }

    private void PositionHandles()
    {
        // Hidden while a new region is being dragged out so they don't get in the way.
        if (_tool != Tool.Select || _sel.IsEmpty || _dragging)
        {
            foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
            _hoverHandle = null;   // a hidden handle never gets its MouseLeave
            return;
        }

        double l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom;
        double cx = (l + r) / 2, cy = (t + b) / 2;
        var pos = new Dictionary<string, Point>
        {
            ["TL"] = new(l, t), ["T"] = new(cx, t), ["TR"] = new(r, t), ["R"] = new(r, cy),
            ["BR"] = new(r, b), ["B"] = new(cx, b), ["BL"] = new(l, b), ["L"] = new(l, cy)
        };
        foreach (var h in _handles)
        {
            string tag = (string)h.Tag;
            // Corner dots always show; a mid dot drops out once its edge is too short
            // to keep it clear of them.
            bool visible = tag.Length == 2
                || (tag is "T" or "B" ? _sel.Width >= EdgeDotMin : _sel.Height >= EdgeDotMin);
            if (!visible) { h.Visibility = Visibility.Collapsed; continue; }

            // The hovered dot grows: both the affordance and a bigger target.
            double size = ReferenceEquals(h, _hoverHandle) ? HandleDotHover : HandleDot;
            var dot = (WpfRect)((Grid)h).Children[0];
            dot.Width = dot.Height = size;
            dot.RadiusX = dot.RadiusY = size / 2;
            h.Width = h.Height = size + HandleHitPad;   // the grab margin around it

            // Centered on its point of the frame, so growing it stays symmetric.
            var p = pos[tag];
            Canvas.SetLeft(h, p.X - h.Width / 2);
            Canvas.SetTop(h, p.Y - h.Height / 2);
            h.Visibility = Visibility.Visible;
        }
    }

    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        _resizing = true;
        _activeHandle = (string)((FrameworkElement)sender).Tag;

        // Handles sit outside the frame and carry a grab margin, so the pointer is never
        // exactly on the edge it drags. Remember the gap and keep it for the whole drag,
        // otherwise the edge snaps to the pointer the moment the button goes down.
        var p = e.GetPosition(Root);
        double ex = _activeHandle.Contains('L') ? _sel.Left
                  : _activeHandle.Contains('R') ? _sel.Right : p.X;
        double ey = _activeHandle.Contains('T') ? _sel.Top
                  : _activeHandle.Contains('B') ? _sel.Bottom : p.Y;
        _resizeGrab = new Vector(ex - p.X, ey - p.Y);

        ((FrameworkElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (!_resizing) return;
        QueueMove(ApplyHandleMove, e.GetPosition(Root));
        e.Handled = true;
    }

    private void ApplyHandleMove(Point raw)
    {
        if (!_resizing) return;
        var p = raw + _resizeGrab;
        double px = Math.Clamp(p.X, 0, Root.ActualWidth);
        double py = Math.Clamp(p.Y, 0, Root.ActualHeight);

        // The corner brackets run 38px along each edge, so a smaller region would let
        // them stick out past the frame; clamp the moving edge against the fixed one.
        const double minSide = 40;
        double l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom;
        switch (_activeHandle)
        {
            case "TL": l = Math.Min(px, r - minSide); t = Math.Min(py, b - minSide); break;
            case "T": t = Math.Min(py, b - minSide); break;
            case "TR": r = Math.Max(px, l + minSide); t = Math.Min(py, b - minSide); break;
            case "R": r = Math.Max(px, l + minSide); break;
            case "BR": r = Math.Max(px, l + minSide); b = Math.Max(py, t + minSide); break;
            case "B": b = Math.Max(py, t + minSide); break;
            case "BL": l = Math.Min(px, r - minSide); b = Math.Max(py, t + minSide); break;
            case "L": l = Math.Min(px, r - minSide); break;
        }

        _sel = new Rect(l, t, r - l, b - t);
        UpdateSelection();
        PositionPanels();
    }

    private void OnHandleUp(object sender, MouseButtonEventArgs e)
    {
        FlushMove(e.GetPosition(Root));   // before _resizing drops, or the last move is lost
        _resizing = false;
        var handle = (FrameworkElement)sender;
        handle.ReleaseMouseCapture();
        // The MouseLeave during the drag was ignored, so settle the hover state here.
        _hoverHandle = handle.IsMouseOver ? handle : null;
        PositionHandles();
        e.Handled = true;
    }
}

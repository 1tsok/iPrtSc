using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using WpfRect = System.Windows.Shapes.Rectangle;

namespace iPrtSc;

public partial class OverlayWindow : Window
{
    private enum Tool { Select, Pen, Marker, Line, Arrow, Rect, Text, Counter, Blur, Move }

    // The highlighter draws much wider than the nominal brush thickness.
    private const double MarkerScale = 3.0;

    private static readonly string[] ColorPresets =
        { "#FFFF3B30", "#FFFFCC00", "#FF34C759", "#FF0A84FF", "#FFFFFFFF", "#FF1C1C1E" };

    private readonly Drawing.Rectangle _bounds;
    private readonly AppSettings _settings;
    private readonly BitmapSource _src;

    // Selection resize handles
    private readonly List<WpfRect> _handles = new();
    private bool _resizing;
    private string _activeHandle = "";

    private double _scale = 1.0;
    private bool _dragging;      // selection drag
    private bool _drawing;       // annotation drag
    private Point _start;
    private Rect _sel = Rect.Empty;

    private Tool _tool = Tool.Select;
    private string _colorHex = "#FFFF3B30";
    private double _thickness = 4;
    private int _counter = 1;

    private Annotation? _current;
    private readonly Stack<UndoItem> _undo = new();
    private readonly Stack<UndoItem> _redo = new();

    // Move tool state
    private bool _moving;
    private TranslateTransform? _moveTransform;
    private Point _moveStart;
    private double _moveOrigX, _moveOrigY;

    public event Action<string>? Saved;
    public event Action? Copied;

    public OverlayWindow(Drawing.Bitmap full, BitmapSource src, Drawing.Rectangle bounds, AppSettings settings)
    {
        InitializeComponent();
        _bounds = bounds;
        _settings = settings;
        _src = src;
        BaseImage.Source = src;

        try
        {
            if (new BrushConverter().ConvertFromString(settings.AccentColor) is Brush b)
            {
                SelBorder.Stroke = b;
                Resources["Accent"] = b;
            }
        }
        catch { /* keep default accent */ }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, NativeMethods.SWP_SHOWWINDOW);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        UpdateDim(Rect.Empty);
        PositionHint();
        BuildColorSwatches();
        BuildHandles();
        ToolSelect.IsChecked = true;
        UpdateUndoRedo();
        Activate();
        Focus();
    }

    // ===== Mouse =====
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Root);
        HideColorFlyout();

        if (_tool == Tool.Move)
        {
            TryBeginMove(p);
            return; // Move never re-selects or clears
        }

        bool insideSelection = !_sel.IsEmpty && _sel.Contains(p);

        if (_tool != Tool.Select && insideSelection)
        {
            BeginAnnotation(p);
            return;
        }

        // Start a new selection (and discard any existing annotations).
        ClearAnnotations();
        _dragging = true;
        _start = p;
        ToolPanel.Visibility = Visibility.Collapsed;
        ActionPanel.Visibility = Visibility.Collapsed;
        foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
        HintPill.Visibility = Visibility.Collapsed;
        Hit.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Root);
        UpdateCursor(p);

        if (_moving && _moveTransform != null)
        {
            var v = p - _moveStart;
            _moveTransform.X = _moveOrigX + v.X;
            _moveTransform.Y = _moveOrigY + v.Y;
            return;
        }

        if (_dragging)
        {
            double x = Math.Max(0, Math.Min(_start.X, p.X));
            double y = Math.Max(0, Math.Min(_start.Y, p.Y));
            double r = Math.Min(Root.ActualWidth, Math.Max(_start.X, p.X));
            double b = Math.Min(Root.ActualHeight, Math.Max(_start.Y, p.Y));
            _sel = new Rect(x, y, Math.Max(0, r - x), Math.Max(0, b - y));
            UpdateSelection();
        }
        else if (_drawing && _current != null)
        {
            var c = Clamp(p, _sel);
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                c = Constrain(_start, c, _tool);
            switch (_current)
            {
                case IFreehandAnnotation fh: fh.Add(c); break;
                case IDragAnnotation dr: dr.Update(_start, c); break;
            }
        }
    }

    /// <summary>Shift constraint: square/circle for shapes, 45° steps for line/arrow.</summary>
    private static Point Constrain(Point a, Point b, Tool t)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        if (t is Tool.Rect)
        {
            double s = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new Point(a.X + Math.Sign(dx) * s, a.Y + Math.Sign(dy) * s);
        }
        if (t is Tool.Line or Tool.Arrow)
        {
            double step = Math.PI / 4;
            double ang = Math.Round(Math.Atan2(dy, dx) / step) * step;
            double len = Math.Sqrt(dx * dx + dy * dy);
            return new Point(a.X + Math.Cos(ang) * len, a.Y + Math.Sin(ang) * len);
        }
        return b;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_moving)
        {
            _moving = false;
            Hit.ReleaseMouseCapture();

            if (_moveTransform is { } tt)
            {
                double ox = _moveOrigX, oy = _moveOrigY, nx = tt.X, ny = tt.Y;
                if (ox != nx || oy != ny)
                    PushUndoItem(
                        undo: () => { tt.X = ox; tt.Y = oy; },
                        redo: () => { tt.X = nx; tt.Y = ny; });
            }
            _moveTransform = null;
            return;
        }

        if (_dragging)
        {
            _dragging = false;
            Hit.ReleaseMouseCapture();
            if (_sel.Width >= 4 && _sel.Height >= 4)
            {
                ToolPanel.Visibility = Visibility.Visible;
                ActionPanel.Visibility = Visibility.Visible;
                PositionPanels();
                ShowHandles();
            }
            else
            {
                _sel = Rect.Empty;
                ClearSelection();
            }
        }
        else if (_drawing)
        {
            _drawing = false;
            Hit.ReleaseMouseCapture();
            CommitCurrent();
        }
    }

    // ===== Annotations =====
    private void BeginAnnotation(Point p)
    {
        var brush = ToBrush(_colorHex);
        var start = Clamp(p, _sel);

        switch (_tool)
        {
            case Tool.Text:
                PlaceText(start, brush);
                return;
            case Tool.Counter:
                PlaceCounter(start, brush);
                return;
            case Tool.Pen:
                var pen = new FreehandAnnotation(brush, _thickness);
                pen.Add(start);
                _current = pen;
                break;
            case Tool.Marker:
                var marker = new HighlighterAnnotation(brush, _thickness * MarkerScale);
                marker.Add(start);
                _current = marker;
                break;
            case Tool.Line:
                _current = new LineAnnotation(brush, _thickness);
                break;
            case Tool.Arrow:
                _current = new ArrowAnnotation(brush, _thickness);
                break;
            case Tool.Rect:
                _current = new RectAnnotation(brush, _thickness);
                break;
            case Tool.Blur:
                _current = new PixelateAnnotation(_src, _scale);
                break;
            default:
                return;
        }

        if (_current is IDragAnnotation d) d.Update(start, start);
        AnnotCanvas.Children.Add(_current.Element);
        _start = start;
        _drawing = true;
        Hit.CaptureMouse();
    }

    private void PlaceText(Point p, Brush brush)
    {
        var ann = new TextAnnotation(brush, 12 + _thickness * 3);
        Canvas.SetLeft(ann.Box, p.X);
        Canvas.SetTop(ann.Box, p.Y);
        AnnotCanvas.Children.Add(ann.Box);
        PushAdd(ann);

        ann.Box.Focusable = true;
        ann.Box.Focus();
        Keyboard.Focus(ann.Box);
    }

    private void PlaceCounter(Point p, Brush brush)
    {
        double d = 16 + _thickness * 2;
        var ann = new CounterAnnotation(brush, _counter++, d);
        Canvas.SetLeft(ann.Element, p.X - d / 2);
        Canvas.SetTop(ann.Element, p.Y - d / 2);
        AnnotCanvas.Children.Add(ann.Element);
        PushAdd(ann);
    }

    private sealed class UndoItem
    {
        public required Action Undo;
        public required Action Redo;
    }

    private void CommitCurrent()
    {
        if (_current == null) return;
        PushAdd(_current);
        _current = null;
    }

    /// <summary>Records an already-added annotation so it can be undone/redone.</summary>
    private void PushAdd(Annotation a)
    {
        bool counter = a is CounterAnnotation;
        PushUndoItem(
            undo: () => { AnnotCanvas.Children.Remove(a.Element); if (counter) _counter = Math.Max(1, _counter - 1); },
            redo: () => { AnnotCanvas.Children.Add(a.Element); if (counter) _counter++; });
    }

    private void PushUndoItem(Action undo, Action redo)
    {
        _undo.Push(new UndoItem { Undo = undo, Redo = redo });
        _redo.Clear();
        UpdateUndoRedo();
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        HideColorFlyout();
        bool hasSel = _sel.Width >= 4 && _sel.Height >= 4;

        MenuItem Item(string header, string gesture, bool enabled, Action action)
        {
            var mi = new MenuItem
            {
                Header = header,
                InputGestureText = gesture,
                IsEnabled = enabled,
                Style = (Style)FindResource("CtxItem")
            };
            if (enabled) mi.Click += (_, _) => action();
            return mi;
        }

        var menu = new ContextMenu { Style = (Style)FindResource("CtxMenu") };
        menu.Items.Add(Item("Copy",              "Enter",  hasSel, DoCopy));
        menu.Items.Add(Item("Save",              "Ctrl+S", hasSel, DoSave));
        menu.Items.Add(new Separator { Style = (Style)FindResource("CtxSep") });
        menu.Items.Add(Item("Select full screen","Ctrl+A", true,   SelectFullScreen));
        menu.Items.Add(Item("Clear selection",   "",       hasSel, DoClearSelection));
        menu.Items.Add(new Separator { Style = (Style)FindResource("CtxSep") });
        menu.Items.Add(Item("Cancel",            "Esc",    true,   Close));

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void SelectFullScreen()
    {
        ClearAnnotations();
        _sel = new Rect(0, 0, Root.ActualWidth, Root.ActualHeight);
        UpdateSelection();
        ToolPanel.Visibility = Visibility.Visible;
        ActionPanel.Visibility = Visibility.Visible;
        PositionPanels();
        ShowHandles();
        HintPill.Visibility = Visibility.Collapsed;
    }

    private void DoClearSelection()
    {
        _sel = Rect.Empty;
        ClearAnnotations();
        ClearSelection();
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var item = _undo.Pop();
        item.Undo();
        _redo.Push(item);
        UpdateUndoRedo();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var item = _redo.Pop();
        item.Redo();
        _undo.Push(item);
        UpdateUndoRedo();
    }

    private void UpdateUndoRedo()
    {
        UndoBtn.IsEnabled = _undo.Count > 0;
        RedoBtn.IsEnabled = _redo.Count > 0;
        UndoBtn.Opacity = UndoBtn.IsEnabled ? 1 : 0.4;
        RedoBtn.Opacity = RedoBtn.IsEnabled ? 1 : 0.4;
    }

    private void ClearAnnotations()
    {
        AnnotCanvas.Children.Clear();
        _undo.Clear();
        _redo.Clear();
        _counter = 1;
        _current = null;
        UpdateUndoRedo();
    }

    // ===== Toolbar state =====
    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        var tag = (string)((ToggleButton)sender).Tag;
        _tool = Enum.Parse<Tool>(tag);
        foreach (var tb in ToolToggles())
            tb.IsChecked = ReferenceEquals(tb, sender);

        HideColorFlyout();
        UpdateCursor(Mouse.GetPosition(Root));
        ShowHandles();
    }

    private IEnumerable<ToggleButton> ToolToggles() => new[]
        { ToolSelect, ToolPen, ToolMarker, ToolLine, ToolArrow, ToolRect, ToolText, ToolCounter, ToolBlur, ToolMove };

    private static bool UsesBrush(Tool t) =>
        t is Tool.Pen or Tool.Marker or Tool.Line or Tool.Arrow or Tool.Rect;

    /// <summary>On-screen brush diameter for the active tool (the marker draws wider).</summary>
    private double EffectiveThickness() => _tool == Tool.Marker ? _thickness * MarkerScale : _thickness;

    private void UpdateCursor(Point p)
    {
        // The "screenshot plane" is the selected region. Only there does the cursor change;
        // over the dimmed backdrop (and the chrome) it stays a plain arrow.
        bool inSel = !_sel.IsEmpty && _sel.Contains(p);

        if (UsesBrush(_tool) && inSel)
        {
            double d = EffectiveThickness();
            BrushCursor.Width = d;
            BrushCursor.Height = d;
            BrushCursor.Stroke = ToBrush(_colorHex);
            Canvas.SetLeft(BrushCursor, p.X - d / 2);
            Canvas.SetTop(BrushCursor, p.Y - d / 2);
            BrushCursor.Visibility = Visibility.Visible;
            Hit.Cursor = Cursors.None;
            return;
        }

        BrushCursor.Visibility = Visibility.Collapsed;
        Hit.Cursor = !inSel
            ? Cursors.Arrow
            : _tool switch
            {
                Tool.Text => Cursors.IBeam,
                Tool.Move => Cursors.SizeAll,
                _ => Cursors.Cross
            };
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (UsesBrush(_tool))
        {
            _thickness = Math.Clamp(_thickness + (e.Delta > 0 ? 1 : -1), 1, 50);
            UpdateCursor(Mouse.GetPosition(Root));
            e.Handled = true;
        }
    }

    // ===== Move =====
    private void TryBeginMove(Point p)
    {
        var result = VisualTreeHelper.HitTest(AnnotCanvas, p);
        if (result?.VisualHit is not DependencyObject hit) return;

        var child = TopLevelChild(hit);
        if (child is not UIElement el) return;

        _moveTransform = EnsureTranslate(el);
        _moveOrigX = _moveTransform.X;
        _moveOrigY = _moveTransform.Y;
        _moveStart = p;
        _moving = true;
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

    private static TranslateTransform EnsureTranslate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t) return t;
        var tt = new TranslateTransform();
        el.RenderTransform = tt;
        return tt;
    }

    private void BuildColorSwatches()
    {
        ColorRow.Children.Clear();
        foreach (var hex in ColorPresets)
        {
            // The colored dot is a true Ellipse (always circular), centered inside a
            // larger transparent Border that hosts the white selection ring. Keeping the
            // ring on the outer container means it can never clip or shrink the dot.
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = ToBrush(hex),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var sw = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(1, 0, 1, 0),
                Tag = hex,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = dot
            };
            sw.MouseLeftButtonDown += OnColorClick;
            ColorRow.Children.Add(sw);
        }
        RefreshColorSelection();
    }

    private void OnColorClick(object sender, MouseButtonEventArgs e)
    {
        _colorHex = (string)((Border)sender).Tag;
        ColorDot.Fill = ToBrush(_colorHex);
        RefreshColorSelection();
        HideColorFlyout();
        // Recolor a focused text box live.
        if (Keyboard.FocusedElement is TextBox tb)
        {
            tb.Foreground = ToBrush(_colorHex);
            tb.CaretBrush = ToBrush(_colorHex);
        }
    }

    private void RefreshColorSelection()
    {
        foreach (var c in ColorRow.Children.OfType<Border>())
            c.BorderThickness = new Thickness(((string)c.Tag).Equals(_colorHex, StringComparison.OrdinalIgnoreCase) ? 2.5 : 0);
    }

    private void OnColorButtonClick(object sender, RoutedEventArgs e)
    {
        if (ColorFlyout.Visibility == Visibility.Visible)
        {
            HideColorFlyout();
            return;
        }

        ColorFlyout.Visibility = Visibility.Visible;
        ColorFlyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double fw = ColorFlyout.DesiredSize.Width, fh = ColorFlyout.DesiredSize.Height;

        // Anchor to the left of the color button, vertically centered on it.
        var anchor = ColorButton.TransformToAncestor(Root).Transform(new Point(0, 0));
        double x = anchor.X - fw - 6;
        if (x < 8) x = anchor.X + ColorButton.ActualWidth + 6; // fall back to the right
        double y = anchor.Y + ColorButton.ActualHeight / 2 - fh / 2;
        y = Math.Max(8, Math.Min(y, Root.ActualHeight - fh - 8));

        Canvas.SetLeft(ColorFlyout, x);
        Canvas.SetTop(ColorFlyout, y);
    }

    private void HideColorFlyout() => ColorFlyout.Visibility = Visibility.Collapsed;

    // ===== Selection visuals =====
    private void UpdateSelection()
    {
        UpdateDim(_sel);
        AnnotCanvas.Clip = new RectangleGeometry(_sel);

        Canvas.SetLeft(SelBorder, _sel.X);
        Canvas.SetTop(SelBorder, _sel.Y);
        SelBorder.Width = _sel.Width;
        SelBorder.Height = _sel.Height;
        SelBorder.Visibility = Visibility.Visible;

        int pw = (int)Math.Round(_sel.Width * _scale);
        int ph = (int)Math.Round(_sel.Height * _scale);
        SizeText.Text = $"{pw} × {ph}";
        SizeLabel.Visibility = Visibility.Visible;
        SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double ly = _sel.Y - SizeLabel.DesiredSize.Height - 6;
        if (ly < 4) ly = _sel.Y + 6;
        Canvas.SetLeft(SizeLabel, _sel.X);
        Canvas.SetTop(SizeLabel, ly);

        PositionHandles();
    }

    private void ClearSelection()
    {
        SelBorder.Visibility = Visibility.Collapsed;
        SizeLabel.Visibility = Visibility.Collapsed;
        ToolPanel.Visibility = Visibility.Collapsed;
        ActionPanel.Visibility = Visibility.Collapsed;
        HideColorFlyout();
        foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
        HintPill.Visibility = Visibility.Visible;
        UpdateDim(Rect.Empty);
    }

    /// <summary>
    /// Centers the hint pill on the monitor under the cursor. The overlay spans the whole
    /// virtual desktop, so a plain horizontal-center would land it in the seam between
    /// monitors; we map the active monitor's physical bounds into the window's DIP space.
    /// </summary>
    private void PositionHint()
    {
        var m = ScreenCapture.CursorMonitorBounds();
        double centerX = (m.Left + m.Width / 2.0 - _bounds.Left) / _scale;
        double top = (m.Top - _bounds.Top) / _scale;

        HintPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(HintPill, centerX - HintPill.DesiredSize.Width / 2);
        Canvas.SetTop(HintPill, top + 48);
    }

    private void PositionPanels()
    {
        const double gap = 10;

        ToolPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = ToolPanel.DesiredSize.Width, th = ToolPanel.DesiredSize.Height;
        double tx = _sel.Right + gap;
        if (tx + tw > Root.ActualWidth - 8) tx = _sel.Left - tw - gap;       // flip to the left
        tx = Math.Max(8, Math.Min(tx, Root.ActualWidth - tw - 8));
        // Anchor the panel to the bottom-right corner of the selection (grows upward).
        double ty = Math.Max(8, Math.Min(_sel.Bottom - th, Root.ActualHeight - th - 8));
        Canvas.SetLeft(ToolPanel, tx);
        Canvas.SetTop(ToolPanel, ty);

        ActionPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double aw = ActionPanel.DesiredSize.Width, ah = ActionPanel.DesiredSize.Height;
        double ax = Math.Max(8, Math.Min(_sel.Right - aw, Root.ActualWidth - aw - 8));
        double ay = _sel.Bottom + gap;
        if (ay + ah > Root.ActualHeight - 8) ay = _sel.Top - ah - gap;        // flip above
        ay = Math.Max(8, Math.Min(ay, Root.ActualHeight - ah - 8));
        Canvas.SetLeft(ActionPanel, ax);
        Canvas.SetTop(ActionPanel, ay);
    }

    private void UpdateDim(Rect s)
    {
        double W = Root.ActualWidth, H = Root.ActualHeight;
        if (s.Width < 1 || s.Height < 1)
        {
            Place(DimTop, 0, 0, W, H);
            Hide(DimLeft); Hide(DimRight); Hide(DimBottom);
            return;
        }
        Place(DimTop, 0, 0, W, s.Top);
        Place(DimLeft, 0, s.Top, s.Left, s.Height);
        Place(DimRight, s.Right, s.Top, W - s.Right, s.Height);
        Place(DimBottom, 0, s.Bottom, W, H - s.Bottom);
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
        var accent = SelBorder.Stroke;
        foreach (var tag in HandleTags)
        {
            var h = new WpfRect
            {
                Width = 11,
                Height = 11,
                Fill = Brushes.White,
                Stroke = accent,
                StrokeThickness = 1.5,
                Tag = tag,
                Visibility = Visibility.Collapsed,
                Cursor = CursorFor(tag)
            };
            h.MouseLeftButtonDown += OnHandleDown;
            h.MouseMove += OnHandleMove;
            h.MouseLeftButtonUp += OnHandleUp;
            HandleCanvas.Children.Add(h);
            _handles.Add(h);
        }
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
        if (_tool != Tool.Select || _sel.IsEmpty)
        {
            foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
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
            var p = pos[(string)h.Tag];
            Canvas.SetLeft(h, p.X - h.Width / 2);
            Canvas.SetTop(h, p.Y - h.Height / 2);
            h.Visibility = Visibility.Visible;
        }
    }

    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        _resizing = true;
        _activeHandle = (string)((WpfRect)sender).Tag;
        ((WpfRect)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (!_resizing) return;
        var p = e.GetPosition(Root);
        double px = Math.Clamp(p.X, 0, Root.ActualWidth);
        double py = Math.Clamp(p.Y, 0, Root.ActualHeight);

        double l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom;
        switch (_activeHandle)
        {
            case "TL": l = px; t = py; break;
            case "T": t = py; break;
            case "TR": r = px; t = py; break;
            case "R": r = px; break;
            case "BR": r = px; b = py; break;
            case "B": b = py; break;
            case "BL": l = px; b = py; break;
            case "L": l = px; break;
        }

        double nl = Math.Min(l, r), nr = Math.Max(l, r);
        double nt = Math.Min(t, b), nb = Math.Max(t, b);
        _sel = new Rect(nl, nt, Math.Max(1, nr - nl), Math.Max(1, nb - nt));
        UpdateSelection();
        PositionPanels();
        e.Handled = true;
    }

    private void OnHandleUp(object sender, MouseButtonEventArgs e)
    {
        _resizing = false;
        ((WpfRect)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private static Point Clamp(Point p, Rect r) =>
        new(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));

    private static Brush ToBrush(string hex)
    {
        try { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
        catch { return Brushes.Red; }
    }

    // ===== Export (WYSIWYG via RenderTargetBitmap) =====
    private BitmapSource ComposeSelection()
    {
        // Drop focus so a text caret isn't captured.
        Keyboard.ClearFocus();

        var hidden = new (UIElement el, Visibility v)[]
        {
            (DimCanvas, DimCanvas.Visibility),
            (SelCanvas, SelCanvas.Visibility),
            (HandleCanvas, HandleCanvas.Visibility),
            (HintPill, HintPill.Visibility),
            (ToolPanel, ToolPanel.Visibility),
            (ActionPanel, ActionPanel.Visibility),
            (ColorFlyout, ColorFlyout.Visibility),
            (BrushCursor, BrushCursor.Visibility)
        };
        foreach (var (el, _) in hidden) el.Visibility = Visibility.Collapsed;
        UpdateLayout();

        double dpi = 96.0 * _scale;
        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Round(Root.ActualWidth * _scale)),
            Math.Max(1, (int)Math.Round(Root.ActualHeight * _scale)),
            dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(Root);

        foreach (var (el, v) in hidden) el.Visibility = v;

        int x = (int)Math.Round(_sel.X * _scale);
        int y = (int)Math.Round(_sel.Y * _scale);
        int w = (int)Math.Round(_sel.Width * _scale);
        int h = (int)Math.Round(_sel.Height * _scale);
        x = Math.Clamp(x, 0, rtb.PixelWidth - 1);
        y = Math.Clamp(y, 0, rtb.PixelHeight - 1);
        w = Math.Clamp(w, 1, rtb.PixelWidth - x);
        h = Math.Clamp(h, 1, rtb.PixelHeight - y);

        var crop = new CroppedBitmap(rtb, new Int32Rect(x, y, w, h));
        crop.Freeze();
        return crop;
    }

    // ===== Actions =====
    private void OnCopy(object sender, RoutedEventArgs e) => DoCopy();
    private void OnSave(object sender, RoutedEventArgs e) => DoSave();
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private const string BuyMeACoffeeUrl = "https://send.monobank.ua/jar/3uGTTGtrPk";

    private void OnBuyCoffee(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BuyMeACoffeeUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Log("OnBuyCoffee", ex); }
    }

    private void DoCopy()
    {
        if (_sel.Width < 1 || _sel.Height < 1) return;
        var image = ComposeSelection();
        ClipboardService.CopyImage(image);
        HistoryService.Archive(image, _settings);
        Copied?.Invoke();
        Close();
    }

    private void DoSave()
    {
        if (_sel.Width < 1 || _sel.Height < 1) return;
        var image = ComposeSelection();

        if (!_settings.AskWhereToSave)
        {
            var path = Path.Combine(SaveService.DefaultFolder(_settings), SaveService.DefaultFileName(_settings));
            SaveService.SaveSource(image, path);
            if (_settings.CopyToClipboardAlways) ClipboardService.CopyImage(image);
            HistoryService.Archive(image, _settings);
            Saved?.Invoke(path);
            Close();
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save screenshot",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FileName = SaveService.DefaultFileName(_settings),
            InitialDirectory = SaveService.DefaultFolder(_settings),
            AddExtension = true,
            OverwritePrompt = true,
            FilterIndex = _settings.SaveFormat.Equals("Jpeg", StringComparison.OrdinalIgnoreCase) ? 2 : 1
        };

        Topmost = false;
        Hide();

        bool? ok = dlg.ShowDialog();
        if (ok == true)
        {
            SaveService.SaveSource(image, dlg.FileName);
            if (_settings.CopyToClipboardAlways) ClipboardService.CopyImage(image);
            HistoryService.Archive(image, _settings);
            Saved?.Invoke(dlg.FileName);
            Close();
        }
        else
        {
            Show();
            Topmost = true;
            Activate();
            Focus();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Let an active text box handle typing itself.
        if (Keyboard.FocusedElement is TextBox)
        {
            if (e.Key == Key.Escape)
            {
                Keyboard.ClearFocus();
                Focus();
                e.Handled = true;
            }
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter) { DoCopy(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { DoSave(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A) { SelectFullScreen(); e.Handled = true; }
    }
}

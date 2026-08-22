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
using Anim = System.Windows.Media.Animation;
using Drawing = System.Drawing;
using WpfRect = System.Windows.Shapes.Rectangle;

namespace iPrtSc;

public partial class OverlayWindow : Window
{
    private enum Tool { Select, Pen, Marker, Line, Arrow, Rect, Ellipse, Text, OcrText, Counter, Stamp, Blur, Move, Picker }

    // The highlighter draws much wider than the nominal brush thickness.
    private const double MarkerScale = 3.0;

    private readonly Drawing.Rectangle _bounds;
    private readonly AppSettings _settings;
    private readonly BitmapSource _src;

    // Quick copy: no toolbar at all, the region lands on the clipboard on mouse release.
    private readonly bool _quickCopy;

    // Live clips, mutated in place: allocating a fresh geometry per mouse move churns
    // the GC during a drag for no reason.
    private readonly RectangleGeometry _clearClip = new();
    private readonly RectangleGeometry _annotClip = new();

    private double _scale = 1.0;
    private bool _dragging;      // selection drag
    private bool _drawing;       // annotation drag
    private Point _start;
    private Rect _sel = Rect.Empty;

    private Tool _tool = Tool.Select;
    private Tool _shape = Tool.Arrow;   // last shape picked from the shapes group
    private Tool _beforePicker = Tool.Select;   // restored after a colour is picked
    private string _colorHex = "#FFE81123";   // red default, replaced in the ctor by the remembered colour
    private double _thickness = 4;

    // Whole-selection drag (Select tool, dragging inside the selected region)
    private bool _movingSel;
    private Point _selMoveStart;
    private Rect _selMoveOrig;

    public event Action<string>? Saved;
    public event Action? Copied;
    public event Action<string>? TextCopied;   // carries the recognized text (for a confirmation toast)

    /// <param name="quickCopy">
    /// Quick copy mode: releasing the mouse over a region copies it straight to the clipboard
    /// and closes the overlay, so the editing panels never appear.
    /// </param>
    public OverlayWindow(Drawing.Bitmap full, BitmapSource src, Drawing.Rectangle bounds, AppSettings settings,
                         bool quickCopy = false)
    {
        InitializeComponent();
        _bounds = bounds;
        _settings = settings;
        _src = src;
        _quickCopy = quickCopy;
        _colorHex = ValidColor(settings.LastColor, _colorHex);
        BaseImage.Source = src;
        ClearImage.Source = src;
        ClearImage.Clip = _clearClip;
        AnnotCanvas.Clip = _annotClip;
        // The capture is shown at exactly the pixel size it was taken at, so filtering can
        // only cost time. It also stops a sub-pixel layout offset from resampling the whole
        // screenshot on every repaint.
        RenderOptions.SetBitmapScalingMode(BaseImage, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(ClearImage, BitmapScalingMode.NearestNeighbor);

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

    protected override void OnClosed(EventArgs e)
    {
        // A move queued right before the overlay closes would keep the render hook alive.
        _moveHandler = null;
        CompositionTarget.Rendering -= OnMoveFrame;
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Root.PreviewMouseDown += OnRootPreviewDown;
        UpdateDim(Rect.Empty);
        BuildColorSwatches();
        UpdateShapesIcon();
        BuildStampPalette();
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
        FlushMove(p);
        HideFlyouts();

        // The eyedropper reads anywhere on the shot, inside the region or out on the dimmed
        // backdrop, and never starts a selection or an annotation.
        if (_tool == Tool.Picker) { PickColor(p); return; }

        if (_tool == Tool.Move)
        {
            // Single click moves, double click edits the text under the pointer.
            if (e.ClickCount == 2 && TextBoxAt(p) is TextBox tb)
            {
                SetEditTarget(null);
                BeginTextEdit(tb, p, isNew: false);
                return;
            }
            TryBeginMove(p);
            return; // Move never re-selects or clears the region
        }

        bool insideSelection = !_sel.IsEmpty && _sel.Contains(p);

        if (_tool == Tool.OcrText)
        {
            if (insideSelection && _ocrWords.Count > 0)
            {
                _ocrSelecting = true;
                _ocrDragStart = p;
                HintPill.Visibility = Visibility.Collapsed;
                Hit.CaptureMouse();
            }
            return;   // never starts a new region or annotation
        }

        // Stamp tool: a click on an existing stamp re-activates it for editing and starts
        // a move drag; a click on empty space places a new stamp.
        if (_tool == Tool.Stamp && insideSelection)
        {
            var hitStamp = _editStamp != null && _editStamp.Contains(p)
                ? _editStamp
                : Enumerable.Reverse(_stamps).FirstOrDefault(s => s.Contains(p));
            if (hitStamp != null)
            {
                ActivateStampEdit(hitStamp);
                BeginGesture();
                _stampMoving = true;
                _stampMoveOffset = p - hitStamp.Center;
                Hit.CaptureMouse();
                return;
            }
        }

        if (_tool != Tool.Select && insideSelection)
        {
            BeginAnnotation(p);
            return;
        }

        // Select tool, click inside the selection → drag the whole region instead of
        // starting over. A drag that begins on the dimmed backdrop still makes a fresh one.
        if (_tool == Tool.Select && insideSelection)
        {
            _movingSel = true;
            _selMoveStart = p;
            _selMoveOrig = _sel;
            Hit.CaptureMouse();
            return;
        }

        // Only the Select tool may redraw the region: with a drawing tool active a stray
        // click on the dimmed backdrop would silently wipe every annotation. (When no
        // region exists at all there is nothing to lose, so any tool may start one.)
        if (_tool != Tool.Select && !_sel.IsEmpty) return;

        // Start a new selection (and discard any existing annotations).
        ClearAnnotations();
        _dragging = true;
        _start = p;
        ToolPanel.Visibility = Visibility.Collapsed;
        foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
        Hit.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Freehand ink keeps every sample, otherwise dropped points would flatten a fast
        // stroke into straight segments. Everything else redraws once per frame.
        if (_drawing && _current is IFreehandAnnotation) ApplyMouseMove(e.GetPosition(Root));
        else QueueMove(ApplyMouseMove, e.GetPosition(Root));
    }

    private void ApplyMouseMove(Point p)
    {
        UpdateCursor(p);

        if (_ocrSelecting)
        {
            SelectWordRange(_ocrDragStart, Clamp(p, _sel));
            return;
        }

        if (_movingSel)
        {
            var v = p - _selMoveStart;
            double nx = Math.Clamp(_selMoveOrig.X + v.X, 0, Root.ActualWidth - _selMoveOrig.Width);
            double ny = Math.Clamp(_selMoveOrig.Y + v.Y, 0, Root.ActualHeight - _selMoveOrig.Height);
            _sel = new Rect(nx, ny, _selMoveOrig.Width, _selMoveOrig.Height);
            UpdateSelection();
            PositionPanels();
            return;
        }

        if (_stampMoving && _editTarget != null)
        {
            _editTarget.Center = Clamp(p - _stampMoveOffset, _sel);
            PositionStampHandles();
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
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (shift) c = Constrain(_start, c, _tool);
            switch (_current)
            {
                // Shift straightens the stroke into a segment from where it started.
                case IFreehandAnnotation fh when shift: fh.SetLine(_start, c); break;
                case IFreehandAnnotation fh: fh.Add(c); break;
                case IDragAnnotation dr: dr.Update(_start, c); break;
            }
        }
    }

    // ===== Mouse-move coalescing =====
    // Pointer moves arrive in bursts, and every one of them used to run a full selection
    // update plus a repaint. Keep only the newest point and apply it once per rendered frame.
    private Action<Point>? _moveHandler;
    private Point _movePoint;

    private void QueueMove(Action<Point> handler, Point p)
    {
        _movePoint = p;
        if (_moveHandler == null) CompositionTarget.Rendering += OnMoveFrame;
        _moveHandler = handler;
    }

    private void OnMoveFrame(object? sender, EventArgs e) => FlushMove();

    /// <summary>
    /// Applies a pending move right away. Called before button-up so the gesture ends on the
    /// real pointer position rather than on a point that is up to a frame stale.
    /// </summary>
    private void FlushMove(Point? at = null)
    {
        var handler = _moveHandler;
        if (handler == null) return;
        _moveHandler = null;
        CompositionTarget.Rendering -= OnMoveFrame;
        handler(at ?? _movePoint);
    }

    /// <summary>Shift constraint: square/circle for shapes, 45° steps for line/arrow.</summary>
    private static Point Constrain(Point a, Point b, Tool t)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        if (t is Tool.Rect or Tool.Ellipse)
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
        // Settle the last coalesced move first: the gesture must end where the pointer is.
        FlushMove(e.GetPosition(Root));

        if (_ocrSelecting)
        {
            _ocrSelecting = false;
            Hit.ReleaseMouseCapture();

            // A click (negligible drag) selects just the word under the pointer.
            if ((e.GetPosition(Root) - _ocrDragStart).Length < 3)
            {
                _selWords.Clear();
                int wi = WordAt(_ocrDragStart);
                if (wi >= 0) _selWords.Add(wi);
                UpdateOcrHighlights();
            }
            return;
        }

        if (_movingSel)
        {
            _movingSel = false;
            Hit.ReleaseMouseCapture();
            return;
        }

        if (_stampMoving)
        {
            _stampMoving = false;
            Hit.ReleaseMouseCapture();
            EndGesture();
            return;
        }

        if (_dragging)
        {
            _dragging = false;
            Hit.ReleaseMouseCapture();
            if (_quickCopy)
            {
                // Straight to the clipboard — no panels, no handles, no second click.
                if (_sel.Width >= 4 && _sel.Height >= 4) QuickCopy();
                else Close();
                return;
            }
            if (_sel.Width >= 4 && _sel.Height >= 4)
            {
                ShowToolBar();
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

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        HideFlyouts();
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
        // In the Grab-text tool Enter copies the text, not the image, so the shortcuts move.
        bool ocr = _tool == Tool.OcrText;
        menu.Items.Add(Item("Copy",              ocr ? "" : "Enter", hasSel, DoCopy));
        if (ocr)
            menu.Items.Add(Item("Copy text",     "Ctrl+C", hasSel, () => _ = DoCopyText()));
        menu.Items.Add(Item("Save",              "Ctrl+S",       hasSel, DoSave));
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

    private static Point Clamp(Point p, Rect r) =>
        new(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));

    private static Brush ToBrush(string hex)
    {
        try { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
        catch { return Brushes.Red; }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Let an active text box handle typing itself.
        if (Keyboard.FocusedElement is TextBox)
        {
            if (e.Key == Key.Escape)
            {
                EndTextEdit();
                e.Handled = true;
            }
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        // Esc first dismisses the object-edit handles; a second Esc closes the overlay.
        if (e.Key == Key.Escape) { if (_editTarget != null) SetEditTarget(null); else Close(); e.Handled = true; }
        // In the Grab-text tool, Enter copies the highlighted text instead of the image.
        else if (e.Key == Key.Enter) { if (_tool == Tool.OcrText) _ = DoCopyText(); else DoCopy(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C && _tool == Tool.OcrText) { _ = DoCopyText(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { DoSave(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A) { SelectAllOrFullScreen(); e.Handled = true; }
        // Del/Backspace removes the object currently showing edit handles.
        else if (e.Key is Key.Delete or Key.Back && _editTarget != null) { DeleteEditTarget(); e.Handled = true; }
        // Single-letter tool shortcuts (no modifiers, only once a selection exists).
        else if (Keyboard.Modifiers == ModifierKeys.None && TryToolHotkey(e.Key)) e.Handled = true;
    }
}

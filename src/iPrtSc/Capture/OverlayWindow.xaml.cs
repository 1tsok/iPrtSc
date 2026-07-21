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
using Drawing = System.Drawing;
using WpfRect = System.Windows.Shapes.Rectangle;

namespace iPrtSc;

public partial class OverlayWindow : Window
{
    private enum Tool { Select, Pen, Marker, Line, Arrow, Rect, Ellipse, Text, OcrText, Counter, Stamp, Blur, Move }

    // The highlighter draws much wider than the nominal brush thickness.
    private const double MarkerScale = 3.0;

    // Default palette, laid out as a 6-column grid (greys, warm, cool, deep, pastel).
    private static readonly string[] ColorPresets =
    {
        "#FF000000", "#FFFFFFFF", "#FFD6D6D6", "#FFA6A6A6", "#FF808080", "#FF5C5C5C",
        "#FFB5176B", "#FFE81123", "#FFFF4500", "#FFFF8C00", "#FFFFB900", "#FFFFF100",
        "#FF8CC63F", "#FF16C60C", "#FF018574", "#FF0099BC", "#FF0050EF", "#FF3A1DB8",
        "#FF8000FF", "#FF5B0E8B", "#FFF2C9A8", "#FFB58455", "#FF8B5A2B", "#FF5A3825",
        "#FFFF80C0", "#FFFFBE7D", "#FFFBF08C", "#FF9CE8AE", "#FF8AD1F5", "#FFC2A8F0",
    };

    private readonly Drawing.Rectangle _bounds;
    private readonly AppSettings _settings;
    private readonly BitmapSource _src;

    // Selection resize handles
    private readonly List<FrameworkElement> _handles = new();
    private bool _resizing;
    private string _activeHandle = "";

    private double _scale = 1.0;
    private bool _dragging;      // selection drag
    private bool _drawing;       // annotation drag
    private Point _start;
    private Rect _sel = Rect.Empty;

    private Tool _tool = Tool.Select;
    private Tool _shape = Tool.Arrow;   // last shape picked from the shapes group
    private string _colorHex = "#FFE81123";   // red, also present in ColorPresets
    private double _thickness = 4;
    private int _counter = 1;

    private Annotation? _current;
    private readonly Stack<UndoItem> _undo = new();
    private readonly Stack<UndoItem> _redo = new();

    // Object editing (Move tool + Stamp tool): the object currently showing the
    // move/scale/rotate handles, and its state at gesture start for undo.
    private IEditTarget? _editTarget;
    private (Point C, double S, double A) _gestureOrig;

    // Whole-selection drag (Select tool, dragging inside the selected region)
    private bool _movingSel;
    private Point _selMoveStart;
    private Rect _selMoveOrig;

    // Stamp tool: placed stamps stay editable (move/scale/rotate) while the tool is active.
    private StampDef _stampDef = StampCatalog.All[0];
    private enum StampInkMode { ForLight, ForDark, Auto }
    private StampInkMode _stampInkMode = StampInkMode.Auto;   // which ink new stamps get
    private readonly List<StampAnnotation> _stamps = new();
    // Pixelate blocks are their own edit targets so moving one re-samples the background.
    private readonly List<PixelateAnnotation> _pixelates = new();
    private StampAnnotation? _editStamp;                   // stamp currently showing handles
    private readonly List<System.Windows.Shapes.Ellipse> _stampCorners = new();
    private Grid? _stampRotHandle;
    private bool _stampScaling, _stampRotating, _stampMoving;
    private double _stampRotOffset;    // stamp angle minus pointer angle at grab
    private Vector _stampMoveOffset;   // pointer minus stamp center at grab

    // OCR text grab: recognized words rendered as selectable boxes over the photo.
    private sealed class OcrWordBox
    {
        public required Rect Rect;       // overlay DIP coordinates
        public required string Text;
        public required int Line;
        public required WpfRect Shape;   // the highlight rectangle on OcrLayer
    }
    private readonly List<OcrWordBox> _ocrWords = new();
    private bool _ocrDone;               // OCR has run for the current selection
    private Rect _ocrRect = Rect.Empty;  // the selection the words were built for
    private bool _ocrSelecting;          // a text-selection drag is in progress
    private Point _ocrDragStart;
    private readonly HashSet<int> _selWords = new();   // indices of currently selected words
    private Brush _ocrIdleFill = Brushes.Transparent;
    private Brush _ocrSelFill = Brushes.Transparent;

    public event Action<string>? Saved;
    public event Action? Copied;
    public event Action<string>? TextCopied;   // carries the recognized text (for a confirmation toast)

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
        HideFlyouts();

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
        ActionPanel.Visibility = Visibility.Collapsed;
        foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
        Hit.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Root);
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
                // A click on existing text edits it instead of stacking a new box on top.
                if (TextBoxAt(p) is TextBox existing) BeginTextEdit(existing, p, isNew: false);
                else PlaceText(start, brush);
                return;
            case Tool.Counter:
                PlaceCounter(start, brush);
                return;
            case Tool.Stamp:
                PlaceStamp(start);
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
            case Tool.Ellipse:
                _current = new EllipseAnnotation(brush, _thickness);
                break;
            case Tool.Blur:
                var pixelate = new PixelateAnnotation(_src, _scale);
                _pixelates.Add(pixelate);
                _current = pixelate;
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

    /// <summary>
    /// Drops a new empty text box and starts editing it. Nothing is pushed on the undo
    /// stack yet: the box is only recorded once editing ends with actual text in it
    /// (see <see cref="OnTextLostFocus"/>), so "add text" undoes as a single step and an
    /// abandoned empty box leaves no trace.
    /// </summary>
    private void PlaceText(Point p, Brush brush)
    {
        var ann = new TextAnnotation(brush, 12 + _thickness * 3);
        Canvas.SetLeft(ann.Box, p.X);
        Canvas.SetTop(ann.Box, p.Y);
        ann.Box.LostKeyboardFocus += OnTextLostFocus;
        ann.Box.ContextMenu = BuildTextMenu(ann.Box);
        AnnotCanvas.Children.Add(ann.Box);
        BeginTextEdit(ann.Box, null, isNew: true);
    }

    // ===== Text editing =====
    private TextBox? _editBox;          // text box currently being edited
    private string _editBoxText = "";   // its content when this edit started
    private bool _editBoxIsNew;         // the box was created by this edit

    /// <summary>The text annotation under the pointer, if any.</summary>
    private TextBox? TextBoxAt(Point p)
    {
        var result = VisualTreeHelper.HitTest(AnnotCanvas, p);
        for (DependencyObject? d = result?.VisualHit; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is TextBox tb) return tb;
        return null;
    }

    /// <summary>
    /// Focuses a text box and, when a click position is given, drops the caret on the
    /// clicked character. The point is translated through the box's own transforms, so
    /// this lands correctly on text the Move tool has scaled or rotated.
    /// </summary>
    private void BeginTextEdit(TextBox box, Point? caretAt, bool isNew)
    {
        // Re-entering the box that is already being edited continues the same session,
        // so the original text and the "was created by this edit" flag survive for undo.
        bool continuing = ReferenceEquals(box, _editBox);

        box.Focusable = true;
        box.Focus();               // commits whatever box was being edited before
        Keyboard.Focus(box);

        if (!continuing)
        {
            _editBox = box;
            _editBoxText = box.Text;
            _editBoxIsNew = isNew;
        }

        if (caretAt is Point p)
            box.CaretIndex = box.GetCharacterIndexFromPoint(Root.TranslatePoint(p, box), true);

        // Hand the mouse to the box itself: with the surface out of the way WPF gives
        // drag-select, word/line selection, Shift+click and the Cut/Copy/Paste menu for
        // free. OnTextLostFocus puts the surface back.
        Hit.IsHitTestVisible = false;
        TextCursor.Visibility = Visibility.Collapsed;
        BrushCursor.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The editing menu, built from the same styles as the overlay's own right-click
    /// menu so it does not fall back to the stock Windows one. Item states are refreshed
    /// on open, since they depend on the selection and on the clipboard.
    /// </summary>
    private ContextMenu BuildTextMenu(TextBox box)
    {
        MenuItem Item(string header, string gesture, Action action)
        {
            var mi = new MenuItem
            {
                Header = header,
                InputGestureText = gesture,
                Style = (Style)FindResource("CtxItem")
            };
            mi.Click += (_, _) => action();
            return mi;
        }

        var cut = Item("Cut", "Ctrl+X", box.Cut);
        var copy = Item("Copy", "Ctrl+C", box.Copy);
        var paste = Item("Paste", "Ctrl+V", box.Paste);
        var all = Item("Select all", "Ctrl+A", box.SelectAll);

        var menu = new ContextMenu { Style = (Style)FindResource("CtxMenu") };
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator { Style = (Style)FindResource("CtxSep") });
        menu.Items.Add(all);

        menu.Opened += (_, _) =>
        {
            bool selected = box.SelectionLength > 0;
            cut.IsEnabled = selected;
            copy.IsEnabled = selected;
            all.IsEnabled = box.Text.Length > 0;
            try { paste.IsEnabled = Clipboard.ContainsText(); }
            catch { paste.IsEnabled = true; }   // clipboard busy: let the paste try
        };
        return menu;
    }

    /// <summary>True when focus has moved into an open context menu rather than away.</summary>
    private static bool InContextMenu(object? focus)
    {
        for (var d = focus as DependencyObject; d != null;
             d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is ContextMenu) return true;
        return false;
    }

    /// <summary>
    /// Ends the active edit by taking keyboard focus off the box. Clearing focus alone
    /// is not enough: the window is a focus scope, so focusing it hands focus straight
    /// back to the box it remembers. Making the box unfocusable drops it for real, and
    /// wiping the scope's remembered element keeps it from coming back.
    /// </summary>
    private void EndTextEdit()
    {
        if (_editBox == null) return;
        _editBox.Focusable = false;   // fires LostKeyboardFocus, which commits the edit
        FocusManager.SetFocusedElement(this, null);
        Keyboard.ClearFocus();
        Focus();
    }

    /// <summary>True when the point falls inside the box currently being edited.</summary>
    private bool OverEditBox(Point p)
    {
        if (_editBox == null) return false;
        var local = Root.TranslatePoint(p, _editBox);
        return local.X >= 0 && local.Y >= 0
            && local.X <= _editBox.ActualWidth && local.Y <= _editBox.ActualHeight;
    }

    private bool IsChrome(DependencyObject? d)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, UiCanvas) || ReferenceEquals(d, HandleCanvas)) return true;
        return false;
    }

    /// <summary>
    /// While a text box is being edited the mouse surface is disabled, so this tunneling
    /// handler is the only thing that still sees clicks. Clicks inside the box are left
    /// alone (native editing); a click outside ends the edit and is re-dispatched to the
    /// surface it was meant for, so dismissing an edit never costs an extra click.
    /// </summary>
    private void OnRootPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (_editBox == null || OverEditBox(e.GetPosition(Root))) return;

        // Toolbars and resize handles keep their own click AND the edit: picking a colour
        // or a size is meant to restyle the text being typed, which only works while the
        // box still holds focus. Focusable chrome ends the edit by itself, through
        // LostKeyboardFocus; the colour swatches are plain Borders, so they never do.
        if (IsChrome(e.OriginalSource as DependencyObject)) return;

        EndTextEdit();

        // The right button is left to its own MouseRightButtonUp on the restored surface.
        if (e.ChangedButton != MouseButton.Left) return;

        e.Handled = true;
        OnMouseDown(Hit, e);
    }

    /// <summary>Turns a finished edit into one undo step (or drops an emptied box).</summary>
    private void OnTextLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box || !ReferenceEquals(box, _editBox)) return;

        // Opening the editing menu takes keyboard focus too; that is not the end of the
        // edit, and focus comes back to the box once the menu closes.
        if (InContextMenu(e.NewFocus)) return;

        Hit.IsHitTestVisible = true;   // the mouse surface goes back on top

        string before = _editBoxText, after = box.Text;
        bool wasNew = _editBoxIsNew;
        _editBox = null;

        if (wasNew)
        {
            if (after.Length == 0) { AnnotCanvas.Children.Remove(box); return; }
            PushUndoItem(
                undo: () => AnnotCanvas.Children.Remove(box),
                redo: () => AnnotCanvas.Children.Add(box));
            return;
        }

        if (after == before) return;

        // Clearing existing text removes the box rather than leaving an invisible target.
        if (after.Length == 0)
        {
            AnnotCanvas.Children.Remove(box);
            PushUndoItem(
                undo: () => { box.Text = before; AnnotCanvas.Children.Add(box); },
                redo: () => AnnotCanvas.Children.Remove(box));
            return;
        }

        PushUndoItem(undo: () => box.Text = before, redo: () => box.Text = after);
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

    private void PlaceStamp(Point p)
    {
        var stamp = new StampAnnotation(_stampDef, _stampInkMode == StampInkMode.ForDark)
        {
            Angle = -5,
            AutoInk = _stampInkMode == StampInkMode.Auto
        };
        stamp.Center = p;
        ResampleAutoInk(stamp);
        AnnotCanvas.Children.Add(stamp.Element);
        _stamps.Add(stamp);
        PushUndoItem(
            undo: () =>
            {
                AnnotCanvas.Children.Remove(stamp.Element);
                _stamps.Remove(stamp);
                if (ReferenceEquals(_editStamp, stamp)) DeactivateStampEdit();
            },
            redo: () => { AnnotCanvas.Children.Add(stamp.Element); _stamps.Add(stamp); });
        ActivateStampEdit(stamp);
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
        var pixelate = a as PixelateAnnotation;
        PushUndoItem(
            undo: () =>
            {
                AnnotCanvas.Children.Remove(a.Element);
                if (counter) _counter = Math.Max(1, _counter - 1);
                if (pixelate != null)
                {
                    _pixelates.Remove(pixelate);
                    if (ReferenceEquals(_editTarget, pixelate)) SetEditTarget(null);
                }
            },
            redo: () =>
            {
                AnnotCanvas.Children.Add(a.Element);
                if (counter) _counter++;
                if (pixelate != null) _pixelates.Add(pixelate);
            });
    }

    private void PushUndoItem(Action undo, Action redo)
    {
        _undo.Push(new UndoItem { Undo = undo, Redo = redo });
        _redo.Clear();
        UpdateUndoRedo();
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
        ToolPanel.Visibility = Visibility.Visible;
        ActionPanel.Visibility = Visibility.Visible;
        PositionPanels();
        ShowHandles();
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
        _stamps.Clear();
        _pixelates.Clear();
        _editBox = null;   // a pending edit must not resurrect a box from the old region
        Hit.IsHitTestVisible = true;
        DeactivateStampEdit();
        ClearOcr();   // recognized words belong to the old selection — drop them
        UpdateUndoRedo();
    }

    // ===== Toolbar state =====
    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        var btn = (ToggleButton)sender;
        var tool = Enum.Parse<Tool>((string)btn.Tag);
        if (tool == Tool.OcrText) { _ = EnterOcrMode(); return; }
        SelectTool(tool, btn);
    }

    /// <summary>Activate a tool and sync the panel toggles + cursor.</summary>
    private void SelectTool(Tool tool, ToggleButton checkedBtn)
    {
        EndTextEdit();   // picking another tool finishes whatever was being typed
        _tool = tool;
        foreach (var tb in ToolToggles())
            tb.IsChecked = ReferenceEquals(tb, checkedBtn);

        OcrLayer.Visibility = tool == Tool.OcrText ? Visibility.Visible : Visibility.Collapsed;
        // Move keeps whatever is selected; Stamp keeps only stamps; other tools drop the target.
        if (tool == Tool.Stamp && _editStamp == null) SetEditTarget(null);
        else if (tool is not (Tool.Stamp or Tool.Move)) SetEditTarget(null);
        else PositionStampHandles();   // re-show handles that were hidden by the previous tool

        HideFlyouts();
        UpdateCursor(Mouse.GetPosition(Root));
        ShowHandles();
    }

    // Shapes group: a click opens the picker right away; the tool activates on shape pick.
    private void OnShapesClick(object sender, RoutedEventArgs e)
    {
        bool wasOpen = ShapesFlyout.Visibility == Visibility.Visible;
        HideFlyouts();
        if (!wasOpen) ShowShapesFlyout();
        // The click flipped the toggle; show the actual tool state instead.
        ShapesGroup.IsChecked = _tool is Tool.Line or Tool.Arrow or Tool.Rect or Tool.Ellipse;
    }

    private void OnShapePick(object sender, RoutedEventArgs e)
    {
        _shape = Enum.Parse<Tool>((string)((ToggleButton)sender).Tag);
        UpdateShapesIcon();
        SelectTool(_shape, ShapesGroup);   // activates the shape and closes the flyout
    }

    // ===== Stamp tool: group button, palette, edit handles =====
    // Same interaction as the shapes group: a click opens the palette right away.
    private void OnStampsClick(object sender, RoutedEventArgs e)
    {
        bool wasOpen = StampFlyout.Visibility == Visibility.Visible;
        HideFlyouts();
        if (!wasOpen) ShowStampFlyout();
        StampGroup.IsChecked = _tool == Tool.Stamp;
    }

    private void ShowStampFlyout()
    {
        StampFlyout.Visibility = Visibility.Visible;
        StampFlyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double fw = StampFlyout.DesiredSize.Width, fh = StampFlyout.DesiredSize.Height;

        var anchor = StampGroup.TransformToAncestor(Root).Transform(new Point(0, 0));
        double x = anchor.X - fw - 6;
        if (x < 8) x = anchor.X + StampGroup.ActualWidth + 6;
        double y = anchor.Y + StampGroup.ActualHeight / 2 - fh / 2;
        y = Math.Max(8, Math.Min(y, Root.ActualHeight - fh - 8));

        Canvas.SetLeft(StampFlyout, x);
        Canvas.SetTop(StampFlyout, y);
    }

    private void HideStampFlyout() => StampFlyout.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Fills the palette with chips previewing every stamp on its target background:
    /// light "paper" for deep inks, dark "slate" for bright ones.
    /// </summary>
    private void BuildStampPalette()
    {
        // Auto previews on paper with deep ink — the placed stamp adapts on its own.
        bool bright = _stampInkMode == StampInkMode.ForDark;
        var chipBg = new SolidColorBrush(bright
            ? Color.FromRgb(0x1B, 0x1D, 0x20)
            : Color.FromRgb(0xF8, 0xF6, 0xF1));
        StampGrid.Children.Clear();
        foreach (var def in StampCatalog.All)
        {
            var chip = new Border
            {
                Width = 112,
                Height = 44,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(6),
                Background = chipBg,
                BorderBrush = (Brush)Resources["Accent"],
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 5, 8, 5),
                Cursor = Cursors.Hand,
                Tag = def,
                ToolTip = def.Text,
                Child = new Viewbox { Stretch = Stretch.Uniform, Child = StampCatalog.BuildVisual(def, bright) }
            };
            chip.MouseLeftButtonDown += OnStampPick;
            StampGrid.Children.Add(chip);
        }
        RefreshStampSelection();
        RefreshInkModeSegments();
    }

    /// <summary>Ink-mode segment click: rebuild the palette in the chosen mode.</summary>
    private void OnInkModePick(object sender, MouseButtonEventArgs e)
    {
        _stampInkMode = ReferenceEquals(sender, InkModeDark) ? StampInkMode.ForDark
                      : ReferenceEquals(sender, InkModeAuto) ? StampInkMode.Auto
                      : StampInkMode.ForLight;
        BuildStampPalette();
        e.Handled = true;
    }

    private void RefreshInkModeSegments()
    {
        InkModeLight.BorderThickness = new Thickness(_stampInkMode == StampInkMode.ForLight ? 1.5 : 0);
        InkModeDark.BorderThickness = new Thickness(_stampInkMode == StampInkMode.ForDark ? 1.5 : 0);
        InkModeAuto.BorderThickness = new Thickness(_stampInkMode == StampInkMode.Auto ? 1.5 : 0);
    }

    // ===== Auto ink: pick deep/bright from the background luminance under the stamp =====

    private byte[]? _lumMap;                               // downscaled Gray8 copy of the screenshot
    private int _lumW, _lumH;
    private const int LumShift = 3;                        // luminance map at 1/8 resolution

    private void EnsureLumMap()
    {
        if (_lumMap != null) return;
        double f = 1.0 / (1 << LumShift);
        var gray = new FormatConvertedBitmap(
            new TransformedBitmap(_src, new ScaleTransform(f, f)), PixelFormats.Gray8, null, 0);
        _lumW = gray.PixelWidth;
        _lumH = gray.PixelHeight;
        _lumMap = new byte[_lumW * _lumH];
        gray.CopyPixels(_lumMap, _lumW, 0);
    }

    /// <summary>Mean luminance (0–255) under the stamp's unrotated bounding box.</summary>
    private double LumUnder(StampAnnotation s)
    {
        EnsureLumMap();
        var c = s.Center;
        var h = s.HalfSize;
        double f = _scale / (1 << LumShift);
        int x0 = Math.Clamp((int)((c.X - h.X) * f), 0, _lumW - 1);
        int x1 = Math.Clamp((int)((c.X + h.X) * f), x0, _lumW - 1);
        int y0 = Math.Clamp((int)((c.Y - h.Y) * f), 0, _lumH - 1);
        int y1 = Math.Clamp((int)((c.Y + h.Y) * f), y0, _lumH - 1);
        long sum = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) sum += _lumMap![y * _lumW + x];
        return (double)sum / ((x1 - x0 + 1) * (y1 - y0 + 1));
    }

    /// <summary>
    /// Live re-pick for Auto stamps. The hysteresis band (128–150) keeps the ink from
    /// flickering while the stamp is dragged across a background near the threshold.
    /// </summary>
    private void ResampleAutoInk(StampAnnotation s)
    {
        if (!s.AutoInk) return;
        double lum = LumUnder(s);
        if (s.BrightInk) { if (lum > 150) s.SetBrightInk(false); }
        else if (lum < 128) s.SetBrightInk(true);
    }

    private void OnStampPick(object sender, MouseButtonEventArgs e)
    {
        _stampDef = (StampDef)((Border)sender).Tag;
        RefreshStampSelection();
        SelectTool(Tool.Stamp, StampGroup);   // activates the tool and closes the flyout
        e.Handled = true;
    }

    private void RefreshStampSelection()
    {
        foreach (var chip in StampGrid.Children.OfType<Border>())
            chip.BorderThickness = new Thickness(ReferenceEquals(chip.Tag, _stampDef) ? 2 : 0);
    }

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

    private ToggleButton[] ShapeButtons() => new[] { ToolArrow, ToolLine, ToolRect, ToolEllipse };

    /// <summary>Highlight the active shape in the picker.</summary>
    private void UpdateShapesIcon()
    {
        foreach (var b in ShapeButtons())
            b.IsChecked = (string)b.Tag == _shape.ToString();
    }

    private IEnumerable<ToggleButton> ToolToggles() => new[]
        { ToolSelect, ToolPen, ToolMarker, ShapesGroup, ToolText, ToolOcr, ToolCounter, StampGroup, ToolBlur, ToolMove };

    private static bool UsesBrush(Tool t) =>
        t is Tool.Pen or Tool.Marker or Tool.Line or Tool.Arrow or Tool.Rect or Tool.Ellipse;

    /// <summary>On-screen brush diameter for the active tool (the marker draws wider).</summary>
    private double EffectiveThickness() => _tool == Tool.Marker ? _thickness * MarkerScale : _thickness;

    private void UpdateCursor(Point p)
    {
        // Keep the move cursor steady while dragging the selection, even when the clamped
        // region briefly trails the pointer at a desktop edge.
        if (_movingSel) { BrushCursor.Visibility = Visibility.Collapsed; Hit.Cursor = Cursors.SizeAll; return; }

        // While a new region is being dragged out the plain arrow is least distracting.
        if (_dragging)
        {
            BrushCursor.Visibility = Visibility.Collapsed;
            TextCursor.Visibility = Visibility.Collapsed;
            Hit.Cursor = Cursors.Arrow;
            return;
        }

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
            TextCursor.Visibility = Visibility.Collapsed;
            Hit.Cursor = Cursors.None;
            return;
        }

        // The counter is a circle, so preview its size with the same brush ring (centred,
        // matching where PlaceCounter drops it).
        if (_tool == Tool.Counter && inSel)
        {
            double d = 16 + _thickness * 2;
            BrushCursor.Width = d;
            BrushCursor.Height = d;
            BrushCursor.Stroke = ToBrush(_colorHex);
            Canvas.SetLeft(BrushCursor, p.X - d / 2);
            Canvas.SetTop(BrushCursor, p.Y - d / 2);
            BrushCursor.Visibility = Visibility.Visible;
            TextCursor.Visibility = Visibility.Collapsed;
            Hit.Cursor = Cursors.None;
            return;
        }

        // Text has no fixed footprint, so preview the font size with an I-beam caret sized to
        // the line height and anchored at the top-left, exactly where PlaceText starts the box.
        if (_tool == Tool.Text && inSel)
        {
            // Over existing text the click edits it, so show the plain I-beam instead of
            // the caret preview that stands for "a new box starts here".
            if (TextBoxAt(p) != null)
            {
                TextCursor.Visibility = Visibility.Collapsed;
                BrushCursor.Visibility = Visibility.Collapsed;
                Hit.Cursor = Cursors.IBeam;
                return;
            }

            double fs = 12 + _thickness * 3;
            double h = fs * 1.05;                      // ~cap-to-descender height of the actual text
            double c = Math.Max(3, h * 0.14);          // half-width of the top/bottom serifs
            TextCursor.Stroke = ToBrush(_colorHex);
            TextCursor.StrokeThickness = Math.Clamp(fs * 0.07, 1.5, 4);
            TextCursor.Data = Geometry.Parse(FormattableString.Invariant(
                $"M {-c},0 L {c},0 M 0,0 L 0,{h} M {-c},{h} L {c},{h}"));
            Canvas.SetLeft(TextCursor, p.X);
            Canvas.SetTop(TextCursor, p.Y);
            TextCursor.Visibility = Visibility.Visible;
            BrushCursor.Visibility = Visibility.Collapsed;
            Hit.Cursor = Cursors.None;
            return;
        }

        BrushCursor.Visibility = Visibility.Collapsed;
        TextCursor.Visibility = Visibility.Collapsed;
        // Before any region exists the crosshair invites drawing one; once a region is
        // there, the backdrop outside it gets the plain arrow.
        Hit.Cursor = !inSel
            ? (_sel.IsEmpty ? Cursors.Cross : Cursors.Arrow)
            : _tool switch
            {
                Tool.Text => Cursors.IBeam,
                Tool.OcrText => Cursors.Arrow,
                Tool.Select => Cursors.SizeAll,   // drag the whole selection to reposition it
                // Move shows the grab cursor only over something it can actually grab.
                Tool.Move => MovableAt(p) ? Cursors.SizeAll : Cursors.Arrow,
                // Over a stamp the click will grab it, elsewhere it will place a new one.
                Tool.Stamp when _editStamp?.Contains(p) == true
                             || _stamps.Any(s => s.Contains(p)) => Cursors.SizeAll,
                _ => Cursors.Cross
            };
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        // Wheel scales the active object (the corner grips do the same by drag).
        if (_tool is Tool.Stamp or Tool.Move && _editTarget != null)
        {
            double factor = e.Delta > 0 ? 1.07 : 1 / 1.07;
            _editTarget.Scale = Math.Clamp(_editTarget.Scale * factor, 0.3, 5);
            PositionStampHandles();
            e.Handled = true;
            return;
        }

        if (UsesBrush(_tool) || _tool is Tool.Text or Tool.Counter)
        {
            _thickness = Math.Clamp(_thickness + (e.Delta > 0 ? 1 : -1), 1, 50);
            if (_editBox != null) _editBox.FontSize = 12 + _thickness * 3;   // resize live
            UpdateCursor(Mouse.GetPosition(Root));
            e.Handled = true;
        }
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

    private void BuildColorSwatches()
    {
        ColorRow.Children.Clear();
        foreach (var hex in ColorPresets)
        {
            // The colored dot is a true Ellipse (always circular), centered inside a larger
            // transparent Border that hosts the accent selection ring. The transparent gap
            // between dot and ring reads as a clean halo on any swatch colour (incl. white/black),
            // and keeping the ring on the outer container means it can never clip or shrink the dot.
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
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(2),
                Tag = hex,
                Background = Brushes.Transparent,
                BorderBrush = (Brush)Resources["Accent"],
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = dot
            };
            sw.MouseLeftButtonDown += OnColorClick;
            ColorRow.Children.Add(sw);
        }
        RefreshColorSelection();
        ColorDot.Fill = ToBrush(_colorHex);   // keep the toolbar dot in sync with the default
    }

    private void OnColorClick(object sender, MouseButtonEventArgs e)
    {
        _colorHex = (string)((Border)sender).Tag;
        ColorDot.Fill = ToBrush(_colorHex);
        RefreshColorSelection();
        HideColorFlyout();
        // Recolor the text box being edited live.
        if (_editBox is TextBox tb)
        {
            tb.Foreground = ToBrush(_colorHex);
            tb.CaretBrush = ToBrush(_colorHex);
        }
    }

    private void RefreshColorSelection()
    {
        foreach (var c in ColorRow.Children.OfType<Border>())
            c.BorderThickness = new Thickness(((string)c.Tag).Equals(_colorHex, StringComparison.OrdinalIgnoreCase) ? 2 : 0);
    }

    private void OnColorButtonClick(object sender, RoutedEventArgs e)
    {
        if (ColorFlyout.Visibility == Visibility.Visible)
        {
            HideColorFlyout();
            return;
        }
        HideShapesFlyout();

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

    private void ShowShapesFlyout()
    {
        ShapesFlyout.Visibility = Visibility.Visible;
        ShapesFlyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double fw = ShapesFlyout.DesiredSize.Width, fh = ShapesFlyout.DesiredSize.Height;

        // Anchor to the left of the shapes button, vertically centered on it.
        var anchor = ShapesGroup.TransformToAncestor(Root).Transform(new Point(0, 0));
        double x = anchor.X - fw - 6;
        if (x < 8) x = anchor.X + ShapesGroup.ActualWidth + 6; // fall back to the right
        double y = anchor.Y + ShapesGroup.ActualHeight / 2 - fh / 2;
        y = Math.Max(8, Math.Min(y, Root.ActualHeight - fh - 8));

        Canvas.SetLeft(ShapesFlyout, x);
        Canvas.SetTop(ShapesFlyout, y);
    }

    private void HideShapesFlyout() => ShapesFlyout.Visibility = Visibility.Collapsed;

    private void HideFlyouts() { HideColorFlyout(); HideShapesFlyout(); HideStampFlyout(); }

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
        HideFlyouts();
        foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
        UpdateDim(Rect.Empty);
    }

    private void PositionPanels()
    {
        // Must clear the resize handles, which stick out past the selection edge.
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
        foreach (var tag in HandleTags)
        {
            // Edge handles are elongated pills along their edge; corner handles are
            // L-shaped brackets whose elbow sits on the selection corner.
            bool horizontal = tag is "T" or "B";
            bool vertical = tag is "L" or "R";
            FrameworkElement h;
            if (horizontal || vertical)
            {
                double w = horizontal ? 76 : 3;
                double ht = vertical ? 76 : 3;
                h = new WpfRect
                {
                    Width = w,
                    Height = ht,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Fill = Brushes.White
                };
            }
            else
            {
                h = BuildCornerHandle(tag);
            }
            h.Tag = tag;
            h.Visibility = Visibility.Collapsed;
            h.Cursor = CursorFor(tag);
            h.MouseLeftButtonDown += OnHandleDown;
            h.MouseMove += OnHandleMove;
            h.MouseLeftButtonUp += OnHandleUp;
            HandleCanvas.Children.Add(h);
            _handles.Add(h);
        }
    }

    // Full length of an edge pill / corner arm, and the corner element's fixed box size
    // (must fit elbow-at-center + arm: 148/2 + 76 > 148 is fine, WPF only clips to layout
    // size when the desired size exceeds it, which it never does here).
    private const double HandleArm = 76;
    private const double CornerBox = 148;

    /// <summary>
    /// An L-shaped corner bracket rendered like the edge pills (white, rounded ends):
    /// two rounded rects unioned into one L geometry. Built for TL with the elbow
    /// centered in the element, then rotated for the other corners so the center-based
    /// positioning in PositionHandles puts the elbow on the selection corner.
    /// </summary>
    private static Grid BuildCornerHandle(string tag)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = CornerGeometry(HandleArm, HandleArm),
            Fill = Brushes.White
        };
        var g = new Grid { Width = CornerBox, Height = CornerBox };
        g.Children.Add(path);
        g.RenderTransformOrigin = new Point(0.5, 0.5);
        g.RenderTransform = new RotateTransform(tag switch
        {
            "TR" => 90,
            "BR" => 180,
            "BL" => 270,
            _ => 0
        });
        return g;
    }

    /// <summary>L geometry for a corner bracket, pre-rotation: arm a1 along X, a2 along Y.</summary>
    private static Geometry CornerGeometry(double a1, double a2)
    {
        const double th = 3, radius = 1.5;
        const double o = CornerBox / 2 - th / 2;   // places the elbow point (th/2, th/2) at center
        return new CombinedGeometry(GeometryCombineMode.Union,
            new RectangleGeometry(new Rect(o, o, a1, th), radius, radius),
            new RectangleGeometry(new Rect(o, o, th, a2), radius, radius));
    }

    /// <summary>
    /// Handle sizing along one selection axis of length s: on small regions the pill and
    /// corner arms shrink to share the edge; when the pill would get too short it is
    /// dropped and the corner arms split the edge between themselves.
    /// </summary>
    private static (double Arm, double Pill, bool ShowPill) SizeAxis(double s)
    {
        const double gap = 6;
        double len = Math.Min(HandleArm, (s - 2 * gap) / 3);
        if (len >= 12) return (len, len, true);
        return (Math.Min(HandleArm, (s - gap) / 2), 0, false);
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
            return;
        }

        double l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom;
        double cx = (l + r) / 2, cy = (t + b) / 2;
        var pos = new Dictionary<string, Point>
        {
            ["TL"] = new(l, t), ["T"] = new(cx, t), ["TR"] = new(r, t), ["R"] = new(r, cy),
            ["BR"] = new(r, b), ["B"] = new(cx, b), ["BL"] = new(l, b), ["L"] = new(l, cy)
        };
        // Handles sit outside the selection border with a small gap: each is pushed
        // outward along its tag's direction far enough that its inner face clears the edge.
        var dir = new Dictionary<string, Vector>
        {
            ["TL"] = new(-1, -1), ["T"] = new(0, -1), ["TR"] = new(1, -1), ["R"] = new(1, 0),
            ["BR"] = new(1, 1), ["B"] = new(0, 1), ["BL"] = new(-1, 1), ["L"] = new(-1, 0)
        };
        // On small regions the handles shrink (and the pills drop out) so nothing
        // sticks out past the frame.
        var (armX, pillX, showPillX) = SizeAxis(_sel.Width);
        var (armY, pillY, showPillY) = SizeAxis(_sel.Height);
        bool showCorners = Math.Min(armX, armY) >= 5;

        const double gap = 0;
        foreach (var h in _handles)
        {
            string tag = (string)h.Tag;
            bool corner = tag.Length == 2;
            bool visible = corner ? showCorners : (tag is "T" or "B" ? showPillX : showPillY);
            if (!visible) { h.Visibility = Visibility.Collapsed; continue; }

            if (tag is "T" or "B") ((WpfRect)h).Width = pillX;
            else if (tag is "L" or "R") ((WpfRect)h).Height = pillY;
            else
            {
                // 90°/270° rotations swap the element's axes, so feed the arms swapped.
                bool swap = tag is "TR" or "BL";
                ((System.Windows.Shapes.Path)((Grid)h).Children[0]).Data =
                    CornerGeometry(swap ? armY : armX, swap ? armX : armY);
            }

            var p = pos[tag];
            var u = dir[tag];
            // Slightly past half the handle's thickness (1.5 for both), so the
            // handles hug the selection frame.
            const double d = gap + 0.25;
            Canvas.SetLeft(h, p.X + u.X * d - h.Width / 2);
            Canvas.SetTop(h, p.Y + u.Y * d - h.Height / 2);
            h.Visibility = Visibility.Visible;
        }
    }

    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        _resizing = true;
        _activeHandle = (string)((FrameworkElement)sender).Tag;
        ((FrameworkElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (!_resizing) return;
        var p = e.GetPosition(Root);
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
        e.Handled = true;
    }

    private void OnHandleUp(object sender, MouseButtonEventArgs e)
    {
        _resizing = false;
        ((FrameworkElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private static Point Clamp(Point p, Rect r) =>
        new(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));

    private static Brush ToBrush(string hex)
    {
        try { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
        catch { return Brushes.Red; }
    }

    // ===== Export =====
    /// <summary>
    /// Builds the final image. The photo layer is copied verbatim from the source
    /// bitmap (CroppedBitmap = a raw pixel copy) so it never passes through WPF's
    /// rendering pipeline — that pipeline dithers on render, which on dark gradients
    /// shows up as scattered bright speckles. Annotations are rendered on their own
    /// transparent layer and alpha-composited over the pristine crop in code.
    /// </summary>
    /// <summary>Selection rectangle mapped into source (physical) pixels, clamped to the bitmap.</summary>
    private Int32Rect SelectionRectPx()
    {
        int x = (int)Math.Round(_sel.X * _scale);
        int y = (int)Math.Round(_sel.Y * _scale);
        int w = (int)Math.Round(_sel.Width * _scale);
        int h = (int)Math.Round(_sel.Height * _scale);
        x = Math.Clamp(x, 0, _src.PixelWidth - 1);
        y = Math.Clamp(y, 0, _src.PixelHeight - 1);
        w = Math.Clamp(w, 1, _src.PixelWidth - x);
        h = Math.Clamp(h, 1, _src.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    /// <summary>The pristine photo crop, without any annotations — what OCR reads.</summary>
    private BitmapSource CropPhoto()
    {
        var crop = new CroppedBitmap(_src, SelectionRectPx());
        crop.Freeze();
        return crop;
    }

    private BitmapSource ComposeSelection()
    {
        // Finish any edit first: an unfinished box would be captured with its caret and,
        // worse, with its selection highlight painted over the text.
        EndTextEdit();
        Keyboard.ClearFocus();

        var px = SelectionRectPx();
        int x = px.X, y = px.Y, w = px.Width, h = px.Height;
        if (AnnotCanvas.Children.Count == 0)
        {
            var plain = new CroppedBitmap(_src, new Int32Rect(x, y, w, h));
            plain.Freeze();
            return plain;
        }

        // Render only the annotation layer (transparent backdrop, already clipped to
        // the selection), crop it to match, then composite over the photo.
        double dpi = 96.0 * _scale;
        var annotRtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Round(Root.ActualWidth * _scale)),
            Math.Max(1, (int)Math.Round(Root.ActualHeight * _scale)),
            dpi, dpi, PixelFormats.Pbgra32);
        annotRtb.Render(AnnotCanvas);

        // Guard against a 1px rounding gap between the source and the rendered layer.
        w = Math.Min(w, annotRtb.PixelWidth - x);
        h = Math.Min(h, annotRtb.PixelHeight - y);
        var rect = new Int32Rect(x, y, w, h);

        var baseCrop = new CroppedBitmap(_src, rect);
        var annotCrop = new CroppedBitmap(annotRtb, rect);

        return CompositeOver(baseCrop, annotCrop);
    }

    /// <summary>
    /// Alpha-composites a premultiplied (Pbgra32) overlay over an opaque photo,
    /// touching only the pixels the overlay actually covers. Output is opaque Bgra32.
    /// </summary>
    private static BitmapSource CompositeOver(BitmapSource photo, BitmapSource overlay)
    {
        int w = photo.PixelWidth, h = photo.PixelHeight;
        int stride = w * 4;

        var baseBgra = new FormatConvertedBitmap(photo, PixelFormats.Bgra32, null, 0);
        var over = overlay.Format == PixelFormats.Pbgra32
            ? overlay
            : new FormatConvertedBitmap(overlay, PixelFormats.Pbgra32, null, 0);

        var bp = new byte[h * stride];
        var op = new byte[h * stride];
        baseBgra.CopyPixels(bp, stride, 0);
        over.CopyPixels(op, stride, 0);

        for (int i = 0; i < bp.Length; i += 4)
        {
            int a = op[i + 3];
            if (a == 0) continue;          // overlay transparent here → keep photo pixel
            int inv = 255 - a;
            // overlay channels are premultiplied, so: out = over + photo * (1 - a)
            bp[i]     = (byte)(op[i]     + bp[i]     * inv / 255);
            bp[i + 1] = (byte)(op[i + 1] + bp[i + 1] * inv / 255);
            bp[i + 2] = (byte)(op[i + 2] + bp[i + 2] * inv / 255);
            bp[i + 3] = 255;
        }

        var outBmp = BitmapSource.Create(w, h, photo.DpiX, photo.DpiY,
            PixelFormats.Bgra32, null, bp, stride);
        outBmp.Freeze();
        return outBmp;
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

    /// <summary>
    /// Copies recognized text. If the user has highlighted specific words (Grab text tool),
    /// copies just those; otherwise copies everything found in the selection.
    /// </summary>
    private async System.Threading.Tasks.Task DoCopyText()
    {
        if (_sel.Width < 1 || _sel.Height < 1) return;
        if (!await EnsureOcrAsync()) return;            // hint already surfaced on failure
        if (_ocrWords.Count == 0) { ShowHint("No text found in selection"); return; }

        string text = OcrText(selectedOnly: _selWords.Count > 0);
        if (string.IsNullOrWhiteSpace(text)) { ShowHint("No text found in selection"); return; }

        ClipboardService.CopyText(text);
        TextCopied?.Invoke(text);
        Close();
    }

    /// <summary>Surfaces a transient status message (OCR feedback) centered over the selection.</summary>
    private void ShowHint(string message)
    {
        HintText.Text = message;
        HintPill.Visibility = Visibility.Visible;

        HintPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = _sel.Left + (_sel.Width - HintPill.DesiredSize.Width) / 2;
        double y = _sel.Top + (_sel.Height - HintPill.DesiredSize.Height) / 2;
        Canvas.SetLeft(HintPill, Math.Max(8, x));
        Canvas.SetTop(HintPill, Math.Max(8, y));
    }

    // ===== OCR text grab =====
    /// <summary>Activates the Grab-text tool and recognizes the selection's text.</summary>
    private async System.Threading.Tasks.Task EnterOcrMode()
    {
        SelectTool(Tool.OcrText, ToolOcr);
        if (!await EnsureOcrAsync()) { SelectTool(Tool.Select, ToolSelect); return; }
        // Success needs no instructions — the highlighted words speak for themselves.
        if (_ocrWords.Count == 0) ShowHint("No text found in selection");
    }

    /// <summary>
    /// Runs OCR for the current selection (once, then cached) and builds the selectable
    /// word boxes. Returns false — surfacing a hint — when OCR is unavailable or errors.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> EnsureOcrAsync()
    {
        if (_ocrDone && _ocrRect == _sel) return true;
        ClearOcr();

        ShowHint("Recognizing text…");
        IReadOnlyList<OcrService.Word> words;
        try { words = await OcrService.RecognizeWordsAsync(CropPhoto()); }
        catch (Exception ex) { Logger.Log("EnsureOcrAsync", ex); ShowHint("Text recognition failed"); return false; }

        _ocrDone = true;
        _ocrRect = _sel;
        BuildOcrWordBoxes(words);
        HintPill.Visibility = Visibility.Collapsed;
        return true;
    }

    private void BuildOcrWordBoxes(IReadOnlyList<OcrService.Word> words)
    {
        var accent = (Resources["Accent"] as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        _ocrIdleFill = Brushes.Transparent;   // the word area is left un-dimmed, so no idle tint
        _ocrSelFill = new SolidColorBrush(Color.FromArgb(0x80, accent.R, accent.G, accent.B));
        _ocrSelFill.Freeze();

        if (words.Count == 0) return;

        // Strongly dim the capture, but punch holes so recognized text stays at full
        // brightness — the high-contrast "text actions" look, via an even-odd geometry
        // (outer selection rect minus the holes).
        var holes = new GeometryGroup { FillRule = FillRule.EvenOdd };
        holes.Children.Add(new RectangleGeometry(_sel));

        var shapes = new List<WpfRect>(words.Count);
        foreach (var w in words)
        {
            // Word boxes come back in source pixels relative to the crop; map to overlay DIP.
            var r = new Rect(_sel.Left + w.X / _scale, _sel.Top + w.Y / _scale,
                             w.Width / _scale, w.Height / _scale);

            // Per-word selection highlight; the veil itself opens per line (below).
            var hl = Rect.Inflate(r, 2, 1);
            var shape = new WpfRect
            {
                Width = hl.Width,
                Height = hl.Height,
                RadiusX = 3,
                RadiusY = 3,
                Fill = _ocrIdleFill
            };
            Canvas.SetLeft(shape, hl.X);
            Canvas.SetTop(shape, hl.Y);
            shapes.Add(shape);
            _ocrWords.Add(new OcrWordBox { Rect = r, Text = w.Text, Line = w.Line, Shape = shape });
        }

        // Open the veil one rectangle per line (the union of the line's words) rather than
        // per word — a continuous band reads cleanly, where ragged per-word cut-outs left
        // half-letters and stray speck-sized holes from any leftover noise.
        foreach (var lineGroup in _ocrWords.GroupBy(b => b.Line))
        {
            Rect band = Rect.Empty;
            foreach (var b in lineGroup) band.Union(b.Rect);
            holes.Children.Add(new RectangleGeometry(Rect.Inflate(band, 3, 2), 4, 4));
        }

        var veil = new System.Windows.Shapes.Path
        {
            Data = holes,
            Fill = new SolidColorBrush(Color.FromArgb(0xB8, 0, 0, 0)),
            IsHitTestVisible = false
        };
        OcrLayer.Children.Add(veil);                 // veil first …
        foreach (var s in shapes) OcrLayer.Children.Add(s);   // … selection highlights on top
    }

    private void ClearOcr()
    {
        OcrLayer.Children.Clear();
        _ocrWords.Clear();
        _selWords.Clear();
        _ocrDone = false;
        _ocrRect = Rect.Empty;
    }

    /// <summary>Index of the word whose box contains the point, or -1 if none does.</summary>
    private int WordAt(Point p)
    {
        for (int i = 0; i < _ocrWords.Count; i++)
            if (_ocrWords[i].Rect.Contains(p)) return i;
        return -1;
    }

    /// <summary>
    /// Caret ordinal (0.._ocrWords.Count) for a point — its reading-order position between
    /// words. Words are already ordered line-by-line, left-to-right.
    /// </summary>
    private int CaretIndex(Point p)
    {
        int i = 0;
        while (i < _ocrWords.Count)
        {
            // Vertical band of this line (union of its word boxes).
            int line = _ocrWords[i].Line, start = i;
            double top = double.MaxValue, bottom = double.MinValue;
            while (i < _ocrWords.Count && _ocrWords[i].Line == line)
            {
                top = Math.Min(top, _ocrWords[i].Rect.Top);
                bottom = Math.Max(bottom, _ocrWords[i].Rect.Bottom);
                i++;
            }
            if (p.Y < top) return start;      // above this line → caret before its first word
            if (p.Y <= bottom)                // inside the band → position within the line
            {
                for (int j = start; j < i; j++)
                    if (p.X < _ocrWords[j].Rect.Left + _ocrWords[j].Rect.Width / 2) return j;
                return i;                     // past the line's last word
            }
        }
        return _ocrWords.Count;               // below every line
    }

    /// <summary>
    /// Selects the words between the anchor and the pointer in reading order — like
    /// dragging over text in an editor, not by the swept rectangle.
    /// </summary>
    private void SelectWordRange(Point anchor, Point cur)
    {
        int a = CaretIndex(anchor), c = CaretIndex(cur);
        int lo = Math.Min(a, c), hi = Math.Max(a, c);   // hi is exclusive

        // A caret landing inside a word still takes the whole word, either direction.
        int wa = WordAt(anchor), wc = WordAt(cur);
        if (wa >= 0) { lo = Math.Min(lo, wa); hi = Math.Max(hi, wa + 1); }
        if (wc >= 0) { lo = Math.Min(lo, wc); hi = Math.Max(hi, wc + 1); }

        _selWords.Clear();
        for (int i = lo; i < hi; i++) _selWords.Add(i);
        UpdateOcrHighlights();
    }

    private void UpdateOcrHighlights()
    {
        for (int i = 0; i < _ocrWords.Count; i++)
            _ocrWords[i].Shape.Fill = _selWords.Contains(i) ? _ocrSelFill : _ocrIdleFill;
    }

    /// <summary>
    /// Recognized text in reading order — a space between words, a newline between lines.
    /// With <paramref name="selectedOnly"/>, restricts output to the highlighted words.
    /// </summary>
    private string OcrText(bool selectedOnly)
    {
        var sb = new StringBuilder();
        int? line = null;
        for (int i = 0; i < _ocrWords.Count; i++)
        {
            if (selectedOnly && !_selWords.Contains(i)) continue;
            var w = _ocrWords[i];
            if (line != null) sb.Append(w.Line != line ? Environment.NewLine : " ");
            sb.Append(w.Text);
            line = w.Line;
        }
        return sb.ToString();
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
    }
}

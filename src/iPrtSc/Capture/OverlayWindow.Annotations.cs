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

/// <summary>Placing annotations on the canvas and the undo/redo stack behind them.</summary>
public partial class OverlayWindow
{
    private int _counter = 1;

    private Annotation? _current;
    private readonly Stack<UndoItem> _undo = new();
    private readonly Stack<UndoItem> _redo = new();

    private readonly List<StampAnnotation> _stamps = new();
    // Pixelate blocks are their own edit targets so moving one re-samples the background.
    private readonly List<PixelateAnnotation> _pixelates = new();

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
}

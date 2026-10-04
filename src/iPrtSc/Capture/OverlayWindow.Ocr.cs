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

/// <summary>Grab text: recognition, the selectable word boxes and copying them out.</summary>
public partial class OverlayWindow
{
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

    private async System.Threading.Tasks.Task DoCopyText()
    {
        if (_sel.Width < 1 || _sel.Height < 1) return;
        if (!await EnsureOcrAsync()) return;            // hint already surfaced on failure
        if (_ocrWords.Count == 0) { ShowHint(Strings.Overlay_Hint_NoText); return; }

        string text = OcrText(selectedOnly: _selWords.Count > 0);
        if (string.IsNullOrWhiteSpace(text)) { ShowHint(Strings.Overlay_Hint_NoText); return; }

        ClipboardService.CopyText(text);
        TextCopied?.Invoke(text);
        Close();
    }

    /// <summary>Activates the Grab-text tool and recognizes the selection's text.</summary>
    private async System.Threading.Tasks.Task EnterOcrMode()
    {
        SelectTool(Tool.OcrText, ToolOcr);
        if (!await EnsureOcrAsync()) { SelectTool(Tool.Select, ToolSelect); return; }
        // Success needs no instructions — the highlighted words speak for themselves.
        if (_ocrWords.Count == 0) ShowHint(Strings.Overlay_Hint_NoText);
    }

    /// <summary>
    /// Runs OCR for the current selection (once, then cached) and builds the selectable
    /// word boxes. Returns false — surfacing a hint — when OCR is unavailable or errors.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> EnsureOcrAsync()
    {
        if (_ocrDone && _ocrRect == _sel) return true;
        ClearOcr();

        ShowHint(Strings.Overlay_Hint_Recognizing);
        ShowBusyVeil();
        IReadOnlyList<OcrService.Word> words;
        try { words = await OcrService.RecognizeWordsAsync(CropPhoto()); }
        catch (Exception ex) { Logger.Log("EnsureOcrAsync", ex); ShowHint(Strings.Overlay_Hint_OcrFailed); return false; }
        finally { HideBusyVeil(); }

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
}

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

/// <summary>Tool bar state, cursors, flyouts and the single-letter tool shortcuts.</summary>
public partial class OverlayWindow
{
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
        // Remember what to hand the pointer back to once a colour has been sampled.
        if (tool == Tool.Picker && _tool != Tool.Picker) _beforePicker = _tool;
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
        => PickShape(Enum.Parse<Tool>((string)((ToggleButton)sender).Tag));   // also closes the flyout

    private ToggleButton[] ShapeButtons() => new[] { ToolArrow, ToolLine, ToolRect, ToolEllipse };

    /// <summary>Highlight the active shape in the picker.</summary>
    private void UpdateShapesIcon()
    {
        foreach (var b in ShapeButtons())
            b.IsChecked = (string)b.Tag == _shape.ToString();
    }

    private IEnumerable<ToggleButton> ToolToggles() => new[]
        { ToolSelect, ToolPen, ToolMarker, ShapesGroup, ToolText, ToolOcr, ToolCounter, StampGroup, ToolBlur, ToolMove, ToolPicker };

    /// <summary>The bar button standing for a tool (shapes and stamps share a group button).</summary>
    private ToggleButton ButtonFor(Tool t) => t switch
    {
        Tool.Pen => ToolPen,
        Tool.Marker => ToolMarker,
        Tool.Line or Tool.Arrow or Tool.Rect or Tool.Ellipse => ShapesGroup,
        Tool.Text => ToolText,
        Tool.OcrText => ToolOcr,
        Tool.Counter => ToolCounter,
        Tool.Stamp => StampGroup,
        Tool.Blur => ToolBlur,
        Tool.Move => ToolMove,
        Tool.Picker => ToolPicker,
        _ => ToolSelect,
    };

    private static bool UsesBrush(Tool t) =>
        t is Tool.Pen or Tool.Marker or Tool.Line or Tool.Arrow or Tool.Rect or Tool.Ellipse;

    /// <summary>On-screen brush diameter for the active tool (the marker draws wider).</summary>
    private double EffectiveThickness() => _tool == Tool.Marker ? _thickness * MarkerScale : _thickness;

    private void UpdateCursor(Point p)
    {
        // The eyedropper replaces every other cursor affordance with its loupe, and unlike the
        // drawing tools it stays armed over the dimmed backdrop too.
        if (_tool == Tool.Picker)
        {
            BrushCursor.Visibility = Visibility.Collapsed;
            TextCursor.Visibility = Visibility.Collapsed;
            Hit.Cursor = Cursors.Cross;
            // Over the toolbar (which sits above the hit surface) there is nothing to sample.
            if (Hit.IsMouseOver) ShowLoupe(p); else Loupe.Visibility = Visibility.Collapsed;
            return;
        }
        Loupe.Visibility = Visibility.Collapsed;

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

    private void OnHitLeave(object sender, MouseEventArgs e) => Loupe.Visibility = Visibility.Collapsed;

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

    private void ShowShapesFlyout() => PlaceFlyout(ShapesFlyout, ShapesGroup);

    /// <summary>
    /// Opens a flyout off the tools bar: centered on the button it belongs to and on the
    /// bar's outer side, so it never covers the shot. Falls back to the other side when
    /// that one is off screen.
    /// </summary>
    private void PlaceFlyout(Border flyout, FrameworkElement button)
    {
        flyout.Visibility = Visibility.Visible;
        flyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double fw = flyout.DesiredSize.Width, fh = flyout.DesiredSize.Height;

        var anchor = button.TransformToAncestor(Root).Transform(new Point(0, 0));
        double x = anchor.X + button.ActualWidth / 2 - fw / 2;
        x = Math.Max(8, Math.Min(x, Root.ActualWidth - fw - 8));

        double top = Canvas.GetTop(ToolPanel), bottom = top + ToolPanel.ActualHeight;
        double y = _toolsAbove ? top - fh - 6 : bottom + 6;
        if (y < 8 || y + fh > Root.ActualHeight - 8) y = _toolsAbove ? bottom + 6 : top - fh - 6;
        y = Math.Max(8, Math.Min(y, Root.ActualHeight - fh - 8));

        Canvas.SetLeft(flyout, x);
        Canvas.SetTop(flyout, y);
    }

    private void HideShapesFlyout() => ShapesFlyout.Visibility = Visibility.Collapsed;

    private void HideFlyouts() { HideColorFlyout(); HideShapesFlyout(); HideStampFlyout(); }

    /// <summary>Maps a bare letter key to a tool; returns false when the key isn't a shortcut.</summary>
    private bool TryToolHotkey(Key key)
    {
        if (ToolPanel.Visibility != Visibility.Visible) return false;   // no selection yet

        switch (key)
        {
            case Key.C: SelectTool(Tool.Select, ToolSelect); return true;
            case Key.V: SelectTool(Tool.Move, ToolMove); return true;
            // Stamps: activate the tool and open the palette, same as clicking the group button.
            case Key.S: SelectTool(Tool.Stamp, StampGroup); ShowStampFlyout(); return true;
            case Key.P: SelectTool(Tool.Pen, ToolPen); return true;
            case Key.M: SelectTool(Tool.Marker, ToolMarker); return true;
            case Key.T: SelectTool(Tool.Text, ToolText); return true;
            case Key.N: SelectTool(Tool.Counter, ToolCounter); return true;
            case Key.B: SelectTool(Tool.Blur, ToolBlur); return true;
            case Key.I: SelectTool(Tool.Picker, ToolPicker); return true;
            case Key.G: _ = EnterOcrMode(); return true;
            case Key.A: PickShape(Tool.Arrow); return true;
            case Key.L: PickShape(Tool.Line); return true;
            case Key.R: PickShape(Tool.Rect); return true;
            case Key.E: PickShape(Tool.Ellipse); return true;
            default: return false;
        }
    }

    /// <summary>Activates a shape and remembers it as the group's current shape.</summary>
    private void PickShape(Tool shape)
    {
        _shape = shape;
        UpdateShapesIcon();
        SelectTool(shape, ShapesGroup);
    }
}

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

/// <summary>The text annotation editing session: focus, caret, menu and commit.</summary>
public partial class OverlayWindow
{
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

        string ctrl = Strings.Common_KeyCtrl;
        var cut = Item(Strings.Common_Cut, ctrl + "+X", box.Cut);
        var copy = Item(Strings.Common_Copy, ctrl + "+C", box.Copy);
        var paste = Item(Strings.Common_Paste, ctrl + "+V", box.Paste);
        var all = Item(Strings.Common_SelectAll, ctrl + "+A", box.SelectAll);

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
}

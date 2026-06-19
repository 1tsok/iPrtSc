using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace iPrtSc;

/// <summary>A WPF-drawn tray context menu, styled to match the rest of the app (Slack/Claude-style).</summary>
public partial class TrayMenuWindow : Window
{
    private bool _closing;

    public TrayMenuWindow() => InitializeComponent();

    public void AddItem(string label, string shortcut, Action action)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 13.5,
            Foreground = (Brush)FindResource("FgItem"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var sc = new TextBlock
        {
            Text = shortcut,
            FontSize = 12,
            Foreground = (Brush)FindResource("FgShortcut"),
            Margin = new Thickness(28, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(sc, 1);
        grid.Children.Add(lbl);
        grid.Children.Add(sc);

        var btn = new Button { Style = (Style)FindResource("MenuItemStyle"), Content = grid };
        btn.Click += (_, _) => { Dismiss(); action(); };
        ItemsHost.Children.Add(btn);
    }

    public void AddSeparator() =>
        ItemsHost.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)FindResource("Sep"),
            Margin = new Thickness(10, 5, 10, 5)
        });

    /// <summary>Shows the menu anchored to the bottom-right of the cursor (typical tray behaviour).</summary>
    public void ShowAtCursor()
    {
        Opacity = 0;
        Show();
        UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(this);
        var cur = Forms.Cursor.Position;                          // physical px
        var work = Forms.Screen.FromPoint(cur).WorkingArea;       // physical px

        double cx = cur.X / dpi.DpiScaleX, cy = cur.Y / dpi.DpiScaleY;
        double wl = work.Left / dpi.DpiScaleX, wt = work.Top / dpi.DpiScaleY;
        double wr = work.Right / dpi.DpiScaleX, wb = work.Bottom / dpi.DpiScaleY;

        double w = ActualWidth, h = ActualHeight;
        // The visible panel sits 16px inside the window (shadow margin); anchor its
        // bottom-right corner near the cursor so the menu pops up and to the left.
        double left = cx - (w - 16);
        double top = cy - (h - 16);

        Left = Math.Max(wl, Math.Min(left, wr - w));
        Top = Math.Max(wt, Math.Min(top, wb - h));

        Opacity = 1;
        Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    private void OnDeactivated(object? sender, EventArgs e) => Dismiss();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; }
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}

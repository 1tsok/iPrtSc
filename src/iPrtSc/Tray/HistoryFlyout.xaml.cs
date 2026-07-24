using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace iPrtSc;

/// <summary>
/// A tray flyout showing thumbnails of the most recent history screenshots.
/// Left-click copies the file to the clipboard; right-click opens it.
/// </summary>
public partial class HistoryFlyout : Window
{
    private bool _closing;

    public HistoryFlyout(IReadOnlyList<string> files, string accent)
    {
        InitializeComponent();
        ApplyAccent(accent);
        Build(files);
    }

    /// <summary>Re-tints the pin's active state with the user's chosen accent colour.</summary>
    private void ApplyAccent(string accent)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(accent);
            Resources["Accent"] = new SolidColorBrush(c);
            Resources["AccentSoft"] = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
        }
        catch { /* keep the default accent on a bad value */ }
    }

    private void Build(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            ThumbHost.Visibility = Visibility.Collapsed;
            ClickHint.Visibility = Visibility.Collapsed; // nothing to click
            EmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        foreach (var path in files)
        {
            var thumb = LoadThumbnail(path);
            if (thumb is null) continue; // skip unreadable/corrupt files

            var img = new Image
            {
                Source = thumb,
                Stretch = Stretch.UniformToFill,
                Clip = new RectangleGeometry(new Rect(0, 0, 110, 72), 6, 6)
            };
            var tile = new Border
            {
                Style = (Style)FindResource("ThumbTile"),
                Child = img,
                ToolTip = $"{Path.GetFileName(path)}\n{File.GetLastWriteTime(path):g}"
            };
            tile.MouseLeftButtonUp += (_, _) => Copy(path);
            tile.MouseRightButtonUp += (_, _) => Open(path);

            ThumbHost.Children.Add(tile);
        }
    }

    /// <summary>Decodes a small thumbnail and releases the file handle (OnLoad) so it stays unlocked.</summary>
    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelWidth = 224; // 2× the tile width for crispness
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex)
        {
            Logger.Log($"HistoryFlyout.LoadThumbnail({path})", ex);
            return null;
        }
    }

    /// <summary>Lets the user reposition the flyout by dragging its header.</summary>
    private void OnDragHandle(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            try { DragMove(); } catch { /* mouse already released — ignore */ }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(HistoryService.HistoryFolder);
            // Pass the path as an argument rather than ShellExecuting the folder directly:
            // the latter can race the shell namespace on a just-created folder.
            System.Diagnostics.Process.Start("explorer.exe", $"\"{HistoryService.HistoryFolder}\"");
        }
        catch (Exception ex) { Logger.Log("HistoryFlyout.OnOpenFolder", ex); }
        DismissUnlessPinned();
    }

    private void Open(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Log("HistoryFlyout.Open", ex); }
        DismissUnlessPinned();
    }

    private void Copy(string path)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            ClipboardService.CopyImage(bi);
        }
        catch (Exception ex) { Logger.Log("HistoryFlyout.Copy", ex); }
        DismissUnlessPinned();
    }

    /// <summary>Shows the flyout anchored to the bottom-right of the cursor (typical tray behaviour).</summary>
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
        // Anchor the visible panel's bottom-right corner near the cursor (16px shadow margin).
        double left = cx - (w - 16);
        double top = cy - (h - 16);

        Left = Math.Max(wl, Math.Min(left, wr - w));
        Top = Math.Max(wt, Math.Min(top, wb - h));

        Opacity = 1;
        Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    /// <summary>When pinned, the flyout ignores focus loss and stays put; otherwise it closes.</summary>
    private void OnDeactivated(object? sender, EventArgs e) => DismissUnlessPinned();

    private bool Pinned => PinToggle.IsChecked == true;

    private void DismissUnlessPinned()
    {
        if (!Pinned) Dismiss();
    }

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

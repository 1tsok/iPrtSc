using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Shapes = System.Windows.Shapes;

namespace iPrtSc;

/// <summary>
/// A tray flyout showing thumbnails of the most recent history screenshots.
/// Left-click copies the file to the clipboard; right-click opens it.
/// </summary>
public partial class HistoryFlyout : Window
{
    // Confirmation-flash icons, drawn on a 24x24 canvas.
    private const string CheckIcon = "M4 12.5 L9.5 18 L20 6.5";
    private const string OpenIcon = "M13.5 4.5h6v6 M19.5 4.5l-8.5 8.5 M17 13.5v5a1.5 1.5 0 0 1-1.5 1.5h-10A1.5 1.5 0 0 1 4 18.5v-10A1.5 1.5 0 0 1 5.5 7h5";
    private const string ErrorIcon = "M6.5 6.5l11 11 M17.5 6.5l-11 11";
    private static readonly Color FlashColor = Colors.White;
    private static readonly Color ErrorColor = Color.FromRgb(0xFF, 0x6B, 0x6B);

    private bool _closing;
    private bool _holdOpen;                                   // ignore focus loss while a flash plays

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

            var flash = BuildFlash();
            var grid = new Grid();
            grid.Children.Add(img);
            grid.Children.Add(flash.Layer);

            var tile = new Border
            {
                Style = (Style)FindResource("ThumbTile"),
                Child = grid,
                ToolTip = $"{Path.GetFileName(path)}\n{File.GetLastWriteTime(path):g}"
            };
            tile.MouseLeftButtonUp += (_, _) => Copy(path, flash);
            tile.MouseRightButtonUp += (_, _) => Open(path, flash);

            ThumbHost.Children.Add(tile);
        }
    }

    /// <summary>Builds the (initially invisible) overlay that confirms an action on a tile.</summary>
    private static TileFlash BuildFlash()
    {
        var icon = new Shapes.Path
        {
            Width = 21,
            Height = 21,
            Stretch = Stretch.Uniform,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var label = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var layer = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x12, 0x12, 0x14)),
            BorderThickness = new Thickness(1.5),
            Opacity = 0,
            IsHitTestVisible = false,                          // clicks must still reach the tile
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, label }
            }
        };
        return new TileFlash(layer, icon, label);
    }

    /// <summary>A tile's confirmation overlay: fades in, holds, fades out.</summary>
    private sealed class TileFlash
    {
        private readonly Shapes.Path _icon;
        private readonly TextBlock _label;

        public TileFlash(Border layer, Shapes.Path icon, TextBlock label)
        {
            Layer = layer; _icon = icon; _label = label;
        }

        public Border Layer { get; }

        public void Play(string text, string geometry, Color color)
        {
            var brush = new SolidColorBrush(color);
            _icon.Data = Geometry.Parse(geometry);
            _icon.Stroke = brush;
            _label.Text = text;
            _label.Foreground = brush;
            Layer.BorderBrush = brush;

            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(410))));
            Layer.BeginAnimation(UIElement.OpacityProperty, fade);
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

    private void Open(string path, TileFlash flash)
    {
        _holdOpen = true;                                      // the launched app steals focus
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Log("HistoryFlyout.Open", ex);
            _holdOpen = false;
            flash.Play("Open failed", ErrorIcon, ErrorColor);   // stay open so the message is readable
            return;
        }
        flash.Play("Opened", OpenIcon, FlashColor);
        DismissAfterFlash();
    }

    private void Copy(string path, TileFlash flash)
    {
        _holdOpen = true;
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
        catch (Exception ex)
        {
            Logger.Log("HistoryFlyout.Copy", ex);
            _holdOpen = false;
            flash.Play("Copy failed", ErrorIcon, ErrorColor);   // e.g. the file was deleted meanwhile
            return;
        }
        flash.Play("Copied", CheckIcon, FlashColor);
        DismissAfterFlash();
    }

    /// <summary>Lets the confirmation flash play out before an unpinned flyout closes.</summary>
    private void DismissAfterFlash()
    {
        if (Pinned) { _holdOpen = false; return; }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(390) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _holdOpen = false;
            Dismiss();
        };
        timer.Start();
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
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_holdOpen) return;                                 // don't cut a confirmation flash short
        DismissUnlessPinned();
    }

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

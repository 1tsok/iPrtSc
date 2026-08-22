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

/// <summary>The eyedropper (loupe + sampling) and the colour palette it feeds.</summary>
public partial class OverlayWindow
{
    // Eyedropper loupe: screenshot pixels shown across, and the on-screen size of that
    // square (the XAML sizes match; the ratio is the zoom factor).
    private const int LoupePixels = 15;
    private const double LoupeSize = 120;

    // Default palette, laid out as a 6-column grid (greys, warm, cool, deep, pastel).
    private static readonly string[] ColorPresets =
    {
        "#FF000000", "#FFFFFFFF", "#FFD6D6D6", "#FFA6A6A6", "#FF808080", "#FF5C5C5C",
        "#FFB5176B", "#FFE81123", "#FFFF4500", "#FFFF8C00", "#FFFFB900", "#FFFFF100",
        "#FF8CC63F", "#FF16C60C", "#FF018574", "#FF0099BC", "#FF0050EF", "#FF3A1DB8",
        "#FF8000FF", "#FF5B0E8B", "#FFF2C9A8", "#FFB58455", "#FF8B5A2B", "#FF5A3825",
        "#FFFF80C0", "#FFFFBE7D", "#FFFBF08C", "#FF9CE8AE", "#FF8AD1F5", "#FFC2A8F0",
    };

    // ===== Eyedropper =====
    // Everything here reads the screenshot itself, never the rendered overlay, so the dim
    // veil and the annotations drawn on top can't tint what the user is shown or handed.

    /// <summary>The screenshot pixel under an overlay point, in device pixels.</summary>
    private (int X, int Y) PixelAt(Point p) =>
        (Math.Clamp((int)(p.X * _scale), 0, _src.PixelWidth - 1),
         Math.Clamp((int)(p.Y * _scale), 0, _src.PixelHeight - 1));

    private Color SampleColor(Point p)
    {
        var (x, y) = PixelAt(p);
        var px = new byte[4];
        _src.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);   // Bgr32, as captured
        return Color.FromRgb(px[2], px[1], px[0]);
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// Parks the loupe beside the pointer and refills it: a crop of the screenshot around
    /// the cursor, scaled up with no filtering so the pixels stay square, plus the reading.
    /// </summary>
    private void ShowLoupe(Point p)
    {
        var (cx, cy) = PixelAt(p);
        var c = SampleColor(p);
        LoupeHex.Text = Hex(c);
        LoupeChip.Background = new SolidColorBrush(c);

        int n = Math.Min(LoupePixels, Math.Min(_src.PixelWidth, _src.PixelHeight));
        int x0 = Math.Clamp(cx - n / 2, 0, _src.PixelWidth - n);
        int y0 = Math.Clamp(cy - n / 2, 0, _src.PixelHeight - n);
        var crop = new CroppedBitmap(_src, new Int32Rect(x0, y0, n, n));
        crop.Freeze();
        LoupeImage.Source = crop;

        // Within half a loupe of a screen edge the crop stops sliding, so the ring has to
        // track the sampled pixel inside it rather than sitting in the middle.
        double cell = LoupeSize / n;
        foreach (var ring in new[] { LoupeCellShadow, LoupeCell })
        {
            ring.Width = cell;
            ring.Height = cell;
            Canvas.SetLeft(ring, (cx - x0) * cell);
            Canvas.SetTop(ring, (cy - y0) * cell);
        }

        Loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lw = Loupe.DesiredSize.Width, lh = Loupe.DesiredSize.Height;
        const double reach = 11;   // just clear of the crosshair, so the eye travels less
        double lx = p.X + reach, ly = p.Y + reach;
        if (lx + lw > Root.ActualWidth - 8) lx = p.X - reach - lw;      // flip rather than clamp, so
        if (ly + lh > Root.ActualHeight - 8) ly = p.Y - reach - lh;     // the pointer is never covered
        Canvas.SetLeft(Loupe, Math.Max(8, lx));
        Canvas.SetTop(Loupe, Math.Max(8, ly));
        Loupe.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Takes the colour under the pointer: it becomes the drawing colour and lands on the
    /// clipboard as #RRGGBB. The previous tool comes back, so a pick is a detour, not a mode.
    /// </summary>
    private void PickColor(Point p)
    {
        var c = SampleColor(p);
        SetColor($"#FF{c.R:X2}{c.G:X2}{c.B:X2}");
        ClipboardService.CopyText(Hex(c));

        var back = _beforePicker is Tool.Picker or Tool.OcrText ? Tool.Select : _beforePicker;
        SelectTool(back, ButtonFor(back));   // also hides the loupe
        ShowHint($"{Hex(c)} copied", TimeSpan.FromMilliseconds(1100));
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
        SetColor((string)((Border)sender).Tag);
        HideColorFlyout();
        // Recolor the text box being edited live.
        if (_editBox is TextBox tb)
        {
            tb.Foreground = ToBrush(_colorHex);
            tb.CaretBrush = ToBrush(_colorHex);
        }
    }

    /// <summary>
    /// The single place the drawing colour changes: swatch clicks and the eyedropper both land
    /// here, so the toolbar dot, the palette ring and the remembered colour never drift apart.
    /// The colour is persisted right away, so the next capture starts where this one left off.
    /// </summary>
    private void SetColor(string hex)
    {
        _colorHex = hex;
        ColorDot.Fill = ToBrush(hex);
        RefreshColorSelection();

        if (string.Equals(_settings.LastColor, hex, StringComparison.OrdinalIgnoreCase)) return;
        _settings.LastColor = hex;
        SettingsStore.Save(_settings);
    }

    /// <summary>Guards the remembered colour: a hand-edited settings file falls back to the default.</summary>
    private static string ValidColor(string? hex, string fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorConverter.ConvertFromString(hex) is Color ? hex : fallback; }
        catch { return fallback; }
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

        PlaceFlyout(ColorFlyout, ColorButton);
    }

    private void HideColorFlyout() => ColorFlyout.Visibility = Visibility.Collapsed;
}

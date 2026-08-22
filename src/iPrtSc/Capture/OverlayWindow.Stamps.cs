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

/// <summary>The stamp palette, its ink modes and the Auto-ink luminance sampling.</summary>
public partial class OverlayWindow
{
    // Stamp tool: placed stamps stay editable (move/scale/rotate) while the tool is active.
    private StampDef _stampDef = StampCatalog.All[0];
    private enum StampInkMode { ForLight, ForDark, Auto }
    private StampInkMode _stampInkMode = StampInkMode.Auto;   // which ink new stamps get

    // ===== Stamp tool: group button, palette, edit handles =====
    // Same interaction as the shapes group: a click opens the palette right away.
    private void OnStampsClick(object sender, RoutedEventArgs e)
    {
        bool wasOpen = StampFlyout.Visibility == Visibility.Visible;
        HideFlyouts();
        if (!wasOpen) ShowStampFlyout();
        StampGroup.IsChecked = _tool == Tool.Stamp;
    }

    private void ShowStampFlyout() => PlaceFlyout(StampFlyout, StampGroup);

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

    // Built on first use: a capture with no Auto stamp never pays for it.
    private LuminanceMap? _lum;
    private LuminanceMap Lum => _lum ??= new LuminanceMap(_src, _scale);

    /// <summary>Live re-pick of an Auto stamp's ink for the background it now sits on.</summary>
    private void ResampleAutoInk(StampAnnotation s)
    {
        // Checked here too, so a plain stamp never triggers the luminance map.
        if (!s.AutoInk) return;
        s.ApplyAutoInk(Lum.Mean(s.Center, s.HalfSize));
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
}

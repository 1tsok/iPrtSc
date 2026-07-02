using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace iPrtSc;

public partial class SettingsWindow : Window
{
    private static readonly string[] AccentPresets =
    {
        "#FF0A84FF", // blue
        "#FF30D158", // green
        "#FF5E5CE6", // indigo
        "#FFFF375F", // pink
        "#FFFF9F0A", // orange
        "#FF64D2FF"  // cyan
    };

    private readonly AppSettings _working;
    private Dictionary<string, FrameworkElement> _panels = null!;
    private string _hkKey;
    private string _hkMods;
    private string _histKey;     // "" => no History hotkey
    private string _histMods;
    private string? _folder;     // null => default
    private string _accent;

    public SettingsWindow(AppSettings working)
    {
        InitializeComponent();

        // Never let the auto-sized window grow past the screen, so the pinned Save/Cancel
        // bar stays on-screen and the body scrolls instead.
        MaxHeight = SystemParameters.WorkArea.Height - 48;

        _working = working;

        _hkKey = working.HotkeyKey;
        _hkMods = working.HotkeyModifiers;
        _histKey = working.HistoryHotkeyKey;
        _histMods = working.HistoryHotkeyModifiers;
        _folder = working.SaveFolder;
        _accent = working.AccentColor;
        Resources["Accent"] = ToBrush(_accent); // live-tint the active controls

        ShowHotkey(HotkeyButton, _hkKey, _hkMods);
        ShowHotkey(HistoryHotkeyButton, _histKey, _histMods);
        AskWhereSwitch.IsChecked = working.AskWhereToSave;
        CopyOnSaveSwitch.IsChecked = working.CopyToClipboardAlways;
        AutoStartSwitch.IsChecked = working.AutoStart;
        FolderButton.Content = FolderDisplay();

        bool jpg = working.SaveFormat.Equals("Jpeg", StringComparison.OrdinalIgnoreCase)
                || working.SaveFormat.Equals("Jpg", StringComparison.OrdinalIgnoreCase);
        FmtJpg.IsChecked = jpg;
        FmtPng.IsChecked = !jpg;

        _panels = new Dictionary<string, FrameworkElement>
        {
            ["Hotkey"] = PanelHotkey,
            ["Saving"] = PanelSaving,
            ["Clipboard"] = PanelClipboard,
            ["Text"] = PanelText,
            ["History"] = PanelHistory,
            ["Appearance"] = PanelAppearance,
            ["System"] = PanelSystem
        };
        NavList.SelectedIndex = 0;

        (working.HistoryRetentionDays switch
        {
            1 => Hist1,
            3 => Hist3,
            7 => Hist7,
            _ => HistOff
        }).IsChecked = true;

        BuildAccentSwatches();
        PopulateOcrLanguages();
    }

    // ---- Text (OCR) ----
    private void PopulateOcrLanguages()
    {
        OcrLangs.Text = string.Join(", ", OcrService.Languages());
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    // ---- Sidebar navigation ----
    private void OnCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is ListBoxItem { Tag: string key })
            foreach (var (k, panel) in _panels)
                panel.Visibility = k == key ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Hotkey capture (shared by the Capture and History fields) ----
    private const string HistoryDisabledText = "Enable history first";

    /// <summary>Shows a binding in a field, dimming the text when there is none ("None").</summary>
    private void ShowHotkey(Button field, string key, string mods)
    {
        field.Content = DisplayOf(key, mods);
        field.Foreground = (Brush)FindResource(string.IsNullOrWhiteSpace(key) ? "Fg3" : "Fg");
    }

    private void EnterListening(Button field)
    {
        field.Content = "Press a key combination…";
        field.Foreground = (Brush)FindResource("Fg3"); // placeholder reads as muted
    }

    private static Key KeyOf(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

    /// <summary>
    /// Reads a complete key combination from the event into <paramref name="key"/> +
    /// <paramref name="mods"/>. Returns false for a lone modifier (keep listening).
    /// </summary>
    private static bool ReadCombo(KeyEventArgs e, out string key, out string mods)
    {
        e.Handled = true;
        key = ""; mods = "None";

        var k = KeyOf(e);
        if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None)
            return false; // modifier alone — wait for the real key

        int vk = KeyInterop.VirtualKeyFromKey(k);
        key = ((Forms.Keys)vk).ToString();

        var m = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) m.Add("Control");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) m.Add("Alt");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) m.Add("Shift");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) m.Add("Win");
        mods = m.Count > 0 ? string.Join(",", m) : "None";
        return true;
    }

    private static string DisplayOf(string key, string mods) =>
        string.IsNullOrWhiteSpace(key) ? "None"
        : (!string.IsNullOrWhiteSpace(mods) && !mods.Equals("None", StringComparison.OrdinalIgnoreCase))
            ? mods.Replace(",", " + ") + " + " + key
            : key;

    // -- Capture hotkey --
    private void OnHotkeyFocus(object sender, RoutedEventArgs e) => EnterListening(HotkeyButton);

    private void OnHotkeyClick(object sender, MouseButtonEventArgs e)
    {
        // GotFocus only fires on the focus transition, so clicking an already-focused
        // field would otherwise do nothing — re-enter listening mode on every click.
        if (HotkeyButton.IsKeyboardFocused)
            EnterListening(HotkeyButton);
    }

    private void OnHotkeyBlur(object sender, RoutedEventArgs e) => ShowHotkey(HotkeyButton, _hkKey, _hkMods);

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (ReadCombo(e, out var key, out var mods))
        {
            _hkKey = key; _hkMods = mods;
            ShowHotkey(HotkeyButton, _hkKey, _hkMods);
        }
    }

    private void OnHotkeyKeyUp(object sender, KeyEventArgs e)
    {
        // The Print Screen key never raises KeyDown in Win32/WPF — only KeyUp — so it
        // must be captured here, otherwise it can't be bound as a hotkey.
        if (KeyOf(e) == Key.Snapshot && ReadCombo(e, out var key, out var mods))
        {
            _hkKey = key; _hkMods = mods;
            ShowHotkey(HotkeyButton, _hkKey, _hkMods);
        }
    }

    // -- History hotkey (optional; Backspace or Delete clears it) --
    private void OnHistoryHotkeyFocus(object sender, RoutedEventArgs e) => EnterListening(HistoryHotkeyButton);

    private void OnHistoryHotkeyClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryHotkeyButton.IsKeyboardFocused)
            EnterListening(HistoryHotkeyButton);
    }

    private void OnHistoryHotkeyBlur(object sender, RoutedEventArgs e)
    {
        if (HistoryHotkeyButton.IsEnabled) ShowHotkey(HistoryHotkeyButton, _histKey, _histMods);
    }

    /// <summary>The History hotkey only does anything while History is on, so disable its field
    /// (and let it explain itself) whenever retention is set to Off.</summary>
    private void OnRetentionChanged(object sender, RoutedEventArgs e) => UpdateHistoryHotkeyEnabled();

    private void UpdateHistoryHotkeyEnabled()
    {
        bool enabled = HistOff.IsChecked != true;
        HistoryHotkeyButton.IsEnabled = enabled;
        if (enabled)
        {
            ShowHotkey(HistoryHotkeyButton, _histKey, _histMods);
        }
        else
        {
            HistoryHotkeyButton.Content = HistoryDisabledText;
            HistoryHotkeyButton.Foreground = (Brush)FindResource("Fg3");
        }
    }

    private void OnHistoryHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (KeyOf(e) is Key.Back or Key.Delete) // clear the binding
        {
            e.Handled = true;
            _histKey = ""; _histMods = "None";
            ShowHotkey(HistoryHotkeyButton, _histKey, _histMods);
            return;
        }
        if (ReadCombo(e, out var key, out var mods))
        {
            _histKey = key; _histMods = mods;
            ShowHotkey(HistoryHotkeyButton, _histKey, _histMods);
        }
    }

    private void OnHistoryHotkeyKeyUp(object sender, KeyEventArgs e)
    {
        if (KeyOf(e) == Key.Snapshot && ReadCombo(e, out var key, out var mods))
        {
            _histKey = key; _histMods = mods;
            ShowHotkey(HistoryHotkeyButton, _histKey, _histMods);
        }
    }

    // ---- Folder ----
    private string FolderDisplay() =>
        string.IsNullOrWhiteSpace(_folder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "iPrtSc") + "  (default)"
            : _folder!;

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose default folder",
            InitialDirectory = string.IsNullOrWhiteSpace(_folder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "iPrtSc")
                : _folder!
        };
        if (dlg.ShowDialog() == true)
        {
            _folder = dlg.FolderName;
            FolderButton.Content = FolderDisplay();
        }
    }

    // ---- Accent ----
    private void BuildAccentSwatches()
    {
        var colors = AccentPresets.ToList();
        if (!colors.Any(c => c.Equals(_accent, StringComparison.OrdinalIgnoreCase)))
            colors.Insert(0, _accent);

        AccentRow.Children.Clear();
        foreach (var hex in colors)
        {
            var swatch = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand,
                Tag = hex,
                Background = ToBrush(hex),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            swatch.MouseLeftButtonDown += OnAccentClick;
            AccentRow.Children.Add(swatch);
        }
        RefreshAccentSelection();
    }

    private void OnAccentClick(object sender, MouseButtonEventArgs e)
    {
        _accent = (string)((Border)sender).Tag;
        Resources["Accent"] = ToBrush(_accent); // update the live preview
        RefreshAccentSelection();
    }

    private void RefreshAccentSelection()
    {
        foreach (var child in AccentRow.Children.OfType<Border>())
        {
            bool selected = ((string)child.Tag).Equals(_accent, StringComparison.OrdinalIgnoreCase);
            child.BorderThickness = new Thickness(selected ? 2.5 : 0);
        }
    }

    private static Brush ToBrush(string hex)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(hex)!; }
        catch { return Brushes.DodgerBlue; }
    }

    // ---- History ----
    private void OnOpenHistory(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(HistoryService.HistoryFolder);
            // Launch Explorer with the path as an argument rather than ShellExecuting the
            // folder directly: the latter can race the shell namespace on a just-created
            // folder and fail with "Location is not available".
            System.Diagnostics.Process.Start("explorer.exe", $"\"{HistoryService.HistoryFolder}\"");
        }
        catch (Exception ex) { Logger.Log("OnOpenHistory", ex); }
    }

    // ---- Buttons ----
    private void OnSave(object sender, RoutedEventArgs e)
    {
        _working.HotkeyKey = _hkKey;
        _working.HotkeyModifiers = _hkMods;
        _working.HistoryHotkeyKey = _histKey;
        _working.HistoryHotkeyModifiers = _histMods;
        _working.AskWhereToSave = AskWhereSwitch.IsChecked == true;
        _working.SaveFolder = _folder;
        _working.SaveFormat = FmtJpg.IsChecked == true ? "Jpeg" : "Png";
        _working.CopyToClipboardAlways = CopyOnSaveSwitch.IsChecked == true;
        _working.AutoStart = AutoStartSwitch.IsChecked == true;
        _working.AccentColor = _accent;
        _working.HistoryRetentionDays =
            Hist7.IsChecked == true ? 7 :
            Hist3.IsChecked == true ? 3 :
            Hist1.IsChecked == true ? 1 : 0;

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

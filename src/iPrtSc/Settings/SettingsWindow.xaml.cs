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
    private string _hkKey;
    private string _hkMods;
    private string? _folder;     // null => default
    private string _accent;

    public SettingsWindow(AppSettings working)
    {
        InitializeComponent();
        _working = working;

        _hkKey = working.HotkeyKey;
        _hkMods = working.HotkeyModifiers;
        _folder = working.SaveFolder;
        _accent = working.AccentColor;
        Resources["Accent"] = ToBrush(_accent); // live-tint the active controls

        HotkeyButton.Content = DisplayOf(_hkKey, _hkMods);
        AskWhereSwitch.IsChecked = working.AskWhereToSave;
        CopyOnSaveSwitch.IsChecked = working.CopyToClipboardAlways;
        AutoStartSwitch.IsChecked = working.AutoStart;
        FolderButton.Content = FolderDisplay();

        bool jpg = working.SaveFormat.Equals("Jpeg", StringComparison.OrdinalIgnoreCase)
                || working.SaveFormat.Equals("Jpg", StringComparison.OrdinalIgnoreCase);
        FmtJpg.IsChecked = jpg;
        FmtPng.IsChecked = !jpg;

        BuildAccentSwatches();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    // ---- Hotkey capture ----
    private void EnterListening()
    {
        HotkeyButton.Content = "Press a key combination…";
        HotkeyHint.Text = "Listening — press the keys you want, e.g. Ctrl + Alt + Home.";
    }

    private void OnHotkeyFocus(object sender, RoutedEventArgs e) => EnterListening();

    private void OnHotkeyClick(object sender, MouseButtonEventArgs e)
    {
        // GotFocus only fires on the focus transition, so clicking an already-focused
        // field would otherwise do nothing — re-enter listening mode on every click.
        if (HotkeyButton.IsKeyboardFocused)
            EnterListening();
    }

    private void OnHotkeyBlur(object sender, RoutedEventArgs e)
    {
        HotkeyButton.Content = DisplayOf(_hkKey, _hkMods);
        HotkeyHint.Text = "Click the field, then press the desired key combination.";
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e) => CaptureHotkey(e);

    private void OnHotkeyKeyUp(object sender, KeyEventArgs e)
    {
        // The Print Screen key never raises KeyDown in Win32/WPF — only KeyUp — so it
        // must be captured here, otherwise it can't be bound as a hotkey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Snapshot)
            CaptureHotkey(e);
    }

    private void CaptureHotkey(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None)
            return; // modifier alone — wait for the real key

        int vk = KeyInterop.VirtualKeyFromKey(key);
        _hkKey = ((Forms.Keys)vk).ToString();

        var mods = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods.Add("Control");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods.Add("Alt");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods.Add("Shift");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods.Add("Win");
        _hkMods = mods.Count > 0 ? string.Join(",", mods) : "None";

        HotkeyButton.Content = DisplayOf(_hkKey, _hkMods);
    }

    private static string DisplayOf(string key, string mods) =>
        (!string.IsNullOrWhiteSpace(mods) && !mods.Equals("None", StringComparison.OrdinalIgnoreCase))
            ? mods.Replace(",", " + ") + " + " + key
            : key;

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

    // ---- Buttons ----
    private void OnSave(object sender, RoutedEventArgs e)
    {
        _working.HotkeyKey = _hkKey;
        _working.HotkeyModifiers = _hkMods;
        _working.AskWhereToSave = AskWhereSwitch.IsChecked == true;
        _working.SaveFolder = _folder;
        _working.SaveFormat = FmtJpg.IsChecked == true ? "Jpeg" : "Png";
        _working.CopyToClipboardAlways = CopyOnSaveSwitch.IsChecked == true;
        _working.AutoStart = AutoStartSwitch.IsChecked == true;
        _working.AccentColor = _accent;

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

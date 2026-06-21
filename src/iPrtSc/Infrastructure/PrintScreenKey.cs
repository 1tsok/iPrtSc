using System;
using Microsoft.Win32;

namespace iPrtSc;

/// <summary>
/// Reads and writes Windows' "Use the Print screen key to open screen capture" setting
/// (HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled). When that shortcut is
/// enabled, Windows steals the bare Print Screen key before our global hotkey sees it, so
/// iPrtSc turns it off to claim the key — and restores it when the hotkey is released.
/// </summary>
public static class PrintScreenKey
{
    private const string KeyPath = @"Control Panel\Keyboard";
    private const string ValueName = "PrintScreenKeyForSnippingEnabled";

    /// <summary>True only for an unmodified Print Screen hotkey (the one Windows intercepts).</summary>
    public static bool IsBarePrintScreen(string key, string mods) =>
        key.Equals("PrintScreen", StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(mods) || mods.Equals("None", StringComparison.OrdinalIgnoreCase));

    public static bool IsBarePrintScreen(AppSettings s) =>
        IsBarePrintScreen(s.HotkeyKey, s.HotkeyModifiers);

    /// <summary>True if Windows currently opens Snipping Tool on Print Screen (value 1, or unset = Win11 default).</summary>
    public static bool SnippingEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            return k?.GetValue(ValueName) switch
            {
                int i => i != 0,
                null => true,   // absent => Windows 11 default is "enabled"
                _ => true
            };
        }
        catch (Exception ex)
        {
            Logger.Log("PrintScreenKey.SnippingEnabled", ex);
            return true;
        }
    }

    /// <summary>Turns the Snipping Tool Print Screen shortcut on/off and nudges the shell to reload it.</summary>
    public static void SetSnipping(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
            k?.SetValue(ValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
            Broadcast();
            Logger.Log($"PrintScreenKey.SetSnipping({enabled})");
        }
        catch (Exception ex)
        {
            Logger.Log("PrintScreenKey.SetSnipping", ex);
        }
    }

    private static void Broadcast()
    {
        // Ask top-level windows (incl. the shell) to re-read keyboard settings without a sign-out.
        NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SETTINGCHANGE,
            IntPtr.Zero, "Keyboard", NativeMethods.SMTO_ABORTIFHUNG, 1000, out _);
    }
}

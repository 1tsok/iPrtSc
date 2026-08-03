using System;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace iPrtSc;

/// <summary>
/// Registers the app's global hotkeys via a hidden message window and raises an event
/// when one fires: <see cref="CapturePressed"/> for Capture, <see cref="HistoryPressed"/>
/// for the optional History hotkey, <see cref="QuickCopyPressed"/> for a no-editing capture
/// and <see cref="FullScreenPressed"/> for the selection-free full screen copy.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    // Distinct ids so the hotkeys can be registered and identified independently.
    private const int CaptureId = 0x4953; // 'IS'
    private const int HistoryId = 0x4954;
    private const int QuickCopyId = 0x4955;
    private const int FullScreenId = 0x4956;

    private readonly HwndSource _src;

    public event Action? CapturePressed;
    public event Action? HistoryPressed;
    public event Action? QuickCopyPressed;
    public event Action? FullScreenPressed;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("iPrtSc.HotkeyWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };
        _src = new HwndSource(p);
        _src.AddHook(Hook);
    }

    /// <summary>Registers the Capture hotkey from the settings. Returns false on failure.</summary>
    public bool RegisterCapture(AppSettings s) =>
        RegisterOne(CaptureId, s.HotkeyKey, s.HotkeyModifiers);

    /// <summary>
    /// Registers the History hotkey if one is set and History is enabled. An unset hotkey
    /// (or disabled History) is a no-op that returns true so the key stays free for other
    /// apps; a configured hotkey that fails to register returns false.
    /// </summary>
    public bool RegisterHistory(AppSettings s)
    {
        NativeMethods.UnregisterHotKey(_src.Handle, HistoryId);
        if (string.IsNullOrWhiteSpace(s.HistoryHotkeyKey) || s.HistoryRetentionDays <= 0)
            return true;
        return RegisterOne(HistoryId, s.HistoryHotkeyKey, s.HistoryHotkeyModifiers);
    }

    /// <summary>
    /// Registers the Quick copy hotkey if one is set. Unset is a no-op that returns true,
    /// so the key stays free for other apps.
    /// </summary>
    public bool RegisterQuickCopy(AppSettings s) =>
        RegisterOptional(QuickCopyId, s.QuickCopyHotkeyKey, s.QuickCopyHotkeyModifiers);

    /// <summary>Registers the Copy full screen hotkey if one is set; see <see cref="RegisterQuickCopy"/>.</summary>
    public bool RegisterFullScreen(AppSettings s) =>
        RegisterOptional(FullScreenId, s.FullScreenHotkeyKey, s.FullScreenHotkeyModifiers);

    private bool RegisterOptional(int id, string keyName, string modifiers)
    {
        NativeMethods.UnregisterHotKey(_src.Handle, id);
        if (string.IsNullOrWhiteSpace(keyName)) return true;
        return RegisterOne(id, keyName, modifiers);
    }

    private bool RegisterOne(int id, string keyName, string modifiers)
    {
        NativeMethods.UnregisterHotKey(_src.Handle, id);

        if (!Enum.TryParse<Forms.Keys>(keyName, ignoreCase: true, out var key))
            return false;

        uint vk = (uint)key;
        uint mods = 0;
        foreach (var part in modifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            mods |= part.ToLowerInvariant() switch
            {
                "alt" => 1u,
                "control" or "ctrl" => 2u,
                "shift" => 4u,
                "win" or "windows" => 8u,
                _ => 0u
            };
        }

        bool ok = NativeMethods.RegisterHotKey(_src.Handle, id, mods | MOD_NOREPEAT, vk);
        Logger.Log($"HotkeyManager.Register id=0x{id:X} vk=0x{vk:X2} mods=0x{mods:X2} handle=0x{_src.Handle.ToInt64():X} ok={ok}");
        return ok;
    }

    /// <summary>
    /// Temporarily releases all global hotkeys so they stop firing — used while the Settings
    /// window is open so the user can press a hotkey into a capture field.
    /// </summary>
    public void UnregisterAll()
    {
        NativeMethods.UnregisterHotKey(_src.Handle, CaptureId);
        NativeMethods.UnregisterHotKey(_src.Handle, HistoryId);
        NativeMethods.UnregisterHotKey(_src.Handle, QuickCopyId);
        NativeMethods.UnregisterHotKey(_src.Handle, FullScreenId);
        Logger.Log("HotkeyManager.UnregisterAll");
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == CaptureId)
            {
                Logger.Log("WM_HOTKEY received (Capture).");
                CapturePressed?.Invoke();
                handled = true;
            }
            else if (id == HistoryId)
            {
                Logger.Log("WM_HOTKEY received (History).");
                HistoryPressed?.Invoke();
                handled = true;
            }
            else if (id == QuickCopyId)
            {
                Logger.Log("WM_HOTKEY received (Quick copy).");
                QuickCopyPressed?.Invoke();
                handled = true;
            }
            else if (id == FullScreenId)
            {
                Logger.Log("WM_HOTKEY received (Full screen).");
                FullScreenPressed?.Invoke();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _src.RemoveHook(Hook);
        _src.Dispose();
    }
}

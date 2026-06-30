using System;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace iPrtSc;

/// <summary>
/// Registers the app's global hotkeys via a hidden message window and raises an event
/// when one fires: <see cref="CapturePressed"/> for Capture, <see cref="HistoryPressed"/>
/// for the optional History hotkey.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    // Distinct ids so the two hotkeys can be registered and identified independently.
    private const int CaptureId = 0x4953; // 'IS'
    private const int HistoryId = 0x4954;

    private readonly HwndSource _src;

    public event Action? CapturePressed;
    public event Action? HistoryPressed;

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

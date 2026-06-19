using System;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace iPrtSc;

/// <summary>
/// Registers a single global hotkey via a hidden message window and
/// raises <see cref="Pressed"/> when it fires.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4953; // 'IS'
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _src;

    public event Action? Pressed;

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

    /// <summary>Registers the hotkey described by the settings. Returns false on failure.</summary>
    public bool Register(AppSettings s)
    {
        NativeMethods.UnregisterHotKey(_src.Handle, HotkeyId);

        if (!Enum.TryParse<Forms.Keys>(s.HotkeyKey, ignoreCase: true, out var key))
            return false;

        uint vk = (uint)key;
        uint mods = 0;
        foreach (var part in s.HotkeyModifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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

        bool ok = NativeMethods.RegisterHotKey(_src.Handle, HotkeyId, mods | MOD_NOREPEAT, vk);
        Logger.Log($"HotkeyManager.Register vk=0x{vk:X2} mods=0x{mods:X2} handle=0x{_src.Handle.ToInt64():X} ok={ok}");
        return ok;
    }

    /// <summary>
    /// Temporarily releases the global hotkey so it stops firing — used while the
    /// Settings window is open so the user can press the hotkey into the capture field.
    /// </summary>
    public void Unregister()
    {
        NativeMethods.UnregisterHotKey(_src.Handle, HotkeyId);
        Logger.Log("HotkeyManager.Unregister");
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Logger.Log("WM_HOTKEY received.");
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        NativeMethods.UnregisterHotKey(_src.Handle, HotkeyId);
        _src.RemoveHook(Hook);
        _src.Dispose();
    }
}

using System;

namespace iPrtSc;

public class AppSettings
{
    /// <summary>Virtual key name (System.Windows.Forms.Keys), e.g. "Home", "PrintScreen".</summary>
    public string HotkeyKey { get; set; } = "Home";

    /// <summary>Comma-separated modifiers: "None", "Control", "Alt", "Shift", "Win" (combinable).</summary>
    public string HotkeyModifiers { get; set; } = "None";

    /// <summary>Optional global hotkey that opens the History flyout. Empty => no hotkey.</summary>
    public string HistoryHotkeyKey { get; set; } = "";

    /// <summary>Modifiers for the History hotkey; same format as <see cref="HotkeyModifiers"/>.</summary>
    public string HistoryHotkeyModifiers { get; set; } = "None";

    /// <summary>"Png" or "Jpeg".</summary>
    public string SaveFormat { get; set; } = "Png";

    /// <summary>Target folder for saved shots; null => Pictures\iPrtSc.</summary>
    public string? SaveFolder { get; set; }

    /// <summary>Also copy to clipboard when saving to a file.</summary>
    public bool CopyToClipboardAlways { get; set; } = false;

    /// <summary>Show a "Save as" dialog (true) or quick-save to the default folder (false).</summary>
    public bool AskWhereToSave { get; set; } = true;

    /// <summary>Launch with Windows.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Selection-frame accent color (ARGB hex).</summary>
    public string AccentColor { get; set; } = "#FF0A84FF";

    /// <summary>Auto-archive captures to %APPDATA%\iPrtSc\history and delete files older than
    /// this many days. 0 => history disabled (no archiving, no submenu). Allowed: 0/1/3/7.</summary>
    public int HistoryRetentionDays { get; set; } = 7;

    /// <summary>True when iPrtSc turned off Windows' "Print Screen opens Snipping Tool" shortcut
    /// to claim the Print Screen hotkey, so it owes a restore when that hotkey is released.</summary>
    public bool RestoreSnippingToolWhenReleased { get; set; } = false;

    /// <summary>UTC of the last GitHub release check; null => never checked.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Latest release version string seen on GitHub, e.g. "0.3.1"; null => unknown.</summary>
    public string? LatestVersionSeen { get; set; }

    /// <summary>HTTP ETag of the last successful release fetch; sent as If-None-Match so an
    /// unchanged release answers 304 (no body, not counted against the API rate limit).</summary>
    public string? LatestEtag { get; set; }

    public string HotkeyDisplay => Display(HotkeyKey, HotkeyModifiers);

    /// <summary>Human-readable History hotkey, or "" when none is set.</summary>
    public string HistoryHotkeyDisplay => Display(HistoryHotkeyKey, HistoryHotkeyModifiers);

    /// <summary>Formats a key + modifiers as "Ctrl + Alt + Home"; returns "" for an unset key.</summary>
    public static string Display(string key, string mods) =>
        string.IsNullOrWhiteSpace(key) ? ""
        : (!string.IsNullOrWhiteSpace(mods) && !mods.Equals("None", StringComparison.OrdinalIgnoreCase))
            ? mods.Replace(",", " + ") + " + " + key
            : key;

    public AppSettings Clone() => new()
    {
        HotkeyKey = HotkeyKey,
        HotkeyModifiers = HotkeyModifiers,
        HistoryHotkeyKey = HistoryHotkeyKey,
        HistoryHotkeyModifiers = HistoryHotkeyModifiers,
        SaveFormat = SaveFormat,
        SaveFolder = SaveFolder,
        CopyToClipboardAlways = CopyToClipboardAlways,
        AskWhereToSave = AskWhereToSave,
        AutoStart = AutoStart,
        AccentColor = AccentColor,
        HistoryRetentionDays = HistoryRetentionDays,
        RestoreSnippingToolWhenReleased = RestoreSnippingToolWhenReleased,
        LastUpdateCheckUtc = LastUpdateCheckUtc,
        LatestVersionSeen = LatestVersionSeen,
        LatestEtag = LatestEtag
    };
}

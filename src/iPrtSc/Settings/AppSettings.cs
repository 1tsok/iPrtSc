using System;

namespace iPrtSc;

public class AppSettings
{
    /// <summary>Virtual key name (System.Windows.Forms.Keys), e.g. "Home", "PrintScreen".</summary>
    public string HotkeyKey { get; set; } = "Home";

    /// <summary>Comma-separated modifiers: "None", "Control", "Alt", "Shift", "Win" (combinable).</summary>
    public string HotkeyModifiers { get; set; } = "None";

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

    /// <summary>UTC of the last GitHub release check; null => never checked.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Latest release version string seen on GitHub, e.g. "0.3.1"; null => unknown.</summary>
    public string? LatestVersionSeen { get; set; }

    public string HotkeyDisplay =>
        (!string.IsNullOrWhiteSpace(HotkeyModifiers) && !HotkeyModifiers.Equals("None", StringComparison.OrdinalIgnoreCase))
            ? HotkeyModifiers.Replace(",", " + ") + " + " + HotkeyKey
            : HotkeyKey;

    public AppSettings Clone() => new()
    {
        HotkeyKey = HotkeyKey,
        HotkeyModifiers = HotkeyModifiers,
        SaveFormat = SaveFormat,
        SaveFolder = SaveFolder,
        CopyToClipboardAlways = CopyToClipboardAlways,
        AskWhereToSave = AskWhereToSave,
        AutoStart = AutoStart,
        AccentColor = AccentColor,
        LastUpdateCheckUtc = LastUpdateCheckUtc,
        LatestVersionSeen = LatestVersionSeen
    };
}

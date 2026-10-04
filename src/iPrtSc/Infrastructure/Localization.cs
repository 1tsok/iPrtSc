using System;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace iPrtSc;

/// <summary>
/// Picks and applies the UI language. Strings live in Resources\Strings*.resx; every window is
/// created on demand, so setting the culture is enough for the next window to pick it up.
/// </summary>
public static class Localization
{
    /// <summary>Supported UI languages: culture code + the name shown in the picker (always native).</summary>
    public static readonly (string Code, string NativeName)[] Languages =
    {
        ("en", "English"),
        ("uk", "Українська"),
        ("pl", "Polski"),
        ("de", "Deutsch"),
        ("es", "Español"),
        ("fr", "Français")
    };

    private const string Fallback = "en";

    /// <summary>The user's Windows display language, captured before <see cref="Apply"/> overrides
    /// the thread culture. (InstalledUICulture is the language Windows was installed with, which
    /// can differ from the display language.)</summary>
    private static readonly CultureInfo SystemUi = CultureInfo.CurrentUICulture;

    /// <summary>Maps a setting ("" = auto, otherwise a culture code) to a supported language code.</summary>
    public static string Resolve(string? setting)
    {
        if (IsSupported(setting)) return setting!.ToLowerInvariant();

        string system = SystemUi.TwoLetterISOLanguageName;
        return IsSupported(system) ? system : Fallback;
    }

    /// <summary>Switches the UI culture for all windows opened from now on.</summary>
    public static void Apply(string? setting)
    {
        var culture = CultureInfo.GetCultureInfo(Resolve(setting));
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>
    /// The language chosen in the installer wizard, or null when there is none (portable copy,
    /// dev build). Used once, to seed the setting on first run.
    /// </summary>
    public static string? ReadInstallerLanguage()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\iPrtSc");
            var value = key?.GetValue("InstallLanguage") as string;
            return IsSupported(value) ? value!.ToLowerInvariant() : null;
        }
        catch (Exception ex)
        {
            Logger.Log("ReadInstallerLanguage", ex);
            return null;
        }
    }

    private static bool IsSupported(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && Languages.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
}

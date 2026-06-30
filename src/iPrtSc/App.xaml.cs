using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace iPrtSc;

public partial class App : Application
{
    private Forms.NotifyIcon _tray = null!;
    private HotkeyManager _hotkey = null!;
    private AppSettings _settings = null!;
    private OverlayWindow? _overlay;
    private TrayMenuWindow? _trayMenu;
    private HistoryFlyout? _historyFlyout;
    private Mutex? _singleInstance;
    private bool _updateAvailable;
    private string? _latestVersion;
    private DispatcherTimer? _updateTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Global exception logging.
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Log("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Log("AppDomain.UnhandledException: " + args.ExceptionObject);

        // Single instance.
        _singleInstance = new Mutex(initiallyOwned: true, "iPrtSc.SingleInstance", out bool isNew);
        if (!isNew)
        {
            Logger.Log("Another instance is already running. Exiting.");
            Shutdown();
            return;
        }

        Logger.Log("=== iPrtSc starting ===");

        try
        {
            _settings = SettingsStore.Load();
            Logger.Log($"Settings loaded. Hotkey={_settings.HotkeyDisplay}");
            AutoStart.Apply(_settings.AutoStart);

            SetupTray();
            SyncPrintScreenOverride();
            SetupHotkey();
            _ = CheckForUpdatesAsync();
            // Re-check periodically so a long-running session still notices new releases;
            // UpdateChecker throttles the actual network calls (and uses an ETag) internally.
            _updateTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromHours(6)
            };
            _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync();
            _updateTimer.Start();
            Task.Run(() => HistoryService.Prune(_settings.HistoryRetentionDays));
            Logger.Log("Startup complete.");
        }
        catch (Exception ex)
        {
            Logger.Log("OnStartup", ex);
        }
    }

    private void SetupHotkey()
    {
        _hotkey = new HotkeyManager();
        _hotkey.CapturePressed += OnHotkeyPressed;
        _hotkey.HistoryPressed += OnHistoryHotkeyPressed;

        bool ok = _hotkey.RegisterCapture(_settings);
        Logger.Log($"RegisterHotKey({_settings.HotkeyDisplay}) => {ok}");
        if (!ok)
            WarnHotkeyFailed(_settings.HotkeyDisplay);

        bool histOk = _hotkey.RegisterHistory(_settings);
        Logger.Log($"RegisterHistoryHotKey({_settings.HistoryHotkeyDisplay}) => {histOk}");
        if (!histOk)
            WarnHotkeyFailed(_settings.HistoryHotkeyDisplay);
    }

    private void WarnHotkeyFailed(string display) =>
        _tray.ShowBalloonTip(3500, "iPrtSc",
            $"Could not register the hotkey \"{display}\" — it may be in use by another app.",
            Forms.ToolTipIcon.Warning);

    private void OnHotkeyPressed()
    {
        Logger.Log("Hotkey pressed.");
        BeginCapture();
    }

    private void OnHistoryHotkeyPressed()
    {
        Logger.Log("History hotkey pressed.");
        if (_settings.HistoryRetentionDays > 0)
            ShowHistoryFlyout();
    }

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = IconFactory.CreateAppIcon(),
            Visible = true,
            Text = TrayTooltip()
        };

        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) BeginCapture();
            else if (e.Button == Forms.MouseButtons.Right) ShowTrayMenu();
        };
    }

    /// <summary>Tray hover text: app version, plus an update note when one is pending.</summary>
    private string TrayTooltip() =>
        _updateAvailable
            ? $"iPrtSc v{UpdateChecker.Current} — update {_latestVersion} available"
            : $"iPrtSc v{UpdateChecker.Current}";

    private void ShowTrayMenu()
    {
        _trayMenu?.Close();

        var menu = new TrayMenuWindow();
        menu.AddItem("Capture", _settings.HotkeyDisplay, BeginCapture);
        if (_settings.HistoryRetentionDays > 0)
            menu.AddItem("History…", _settings.HistoryHotkeyDisplay, ShowHistoryFlyout);
        menu.AddItem("Settings…", "", OpenSettings);
        menu.AddItem("About", "", OpenAbout, badge: _updateAvailable);
        menu.AddSeparator();
        menu.AddItem("Exit", "", ExitApp);
        menu.Closed += (_, _) => { if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null; };

        _trayMenu = menu;
        menu.ShowAtCursor();
    }

    private void ShowHistoryFlyout()
    {
        try
        {
            _historyFlyout?.Close();
            var files = HistoryService.Recent(12);
            var fly = new HistoryFlyout(files);
            fly.Closed += (_, _) => { if (ReferenceEquals(_historyFlyout, fly)) _historyFlyout = null; };
            _historyFlyout = fly;
            fly.ShowAtCursor();
        }
        catch (Exception ex) { Logger.Log("ShowHistoryFlyout", ex); }
    }

    // ===== Print Screen key override (Windows 11 Snipping Tool shortcut) =====

    /// <summary>
    /// On startup: if the hotkey is the bare Print Screen key and Windows still steals it for
    /// Snipping Tool, turn that shortcut off once so our global hotkey can fire. Idempotent —
    /// the flag means we only claim it a single time, never fighting a user who re-enables it.
    /// </summary>
    private void SyncPrintScreenOverride()
    {
        try
        {
            if (PrintScreenKey.IsBarePrintScreen(_settings)
                && !_settings.RestoreSnippingToolWhenReleased
                && PrintScreenKey.SnippingEnabled())
            {
                PrintScreenKey.SetSnipping(false);
                _settings.RestoreSnippingToolWhenReleased = true;
                SettingsStore.Save(_settings);
                Logger.Log("Print Screen claimed at startup; Snipping Tool shortcut disabled.");
            }
        }
        catch (Exception ex) { Logger.Log("SyncPrintScreenOverride", ex); }
    }

    /// <summary>
    /// Settles the Snipping Tool shortcut after the Settings dialog closes. The dialog runs with
    /// the shortcut temporarily disabled (so Print Screen can be pressed into the capture field);
    /// here we decide the lasting state from the final hotkey:
    ///  • Print Screen is the hotkey  → keep it disabled; remember to restore later iff it was on.
    ///  • anything else               → restore it to its pre-dialog state (or to on if we owed it).
    /// <paramref name="snippingWasOn"/> is the real state captured before the dialog opened.
    /// </summary>
    private void FinalizePrintScreen(bool snippingWasOn, bool wasBarePrtSc, bool nowBarePrtSc)
    {
        try
        {
            if (nowBarePrtSc)
            {
                // Snipping stays off (already disabled for the dialog). We owe a restore if it was on.
                if (snippingWasOn) _settings.RestoreSnippingToolWhenReleased = true;
                SettingsStore.Save(_settings);

                if (snippingWasOn && !wasBarePrtSc)
                    _tray.ShowBalloonTip(6000, "iPrtSc",
                        "Print Screen now opens iPrtSc. Windows' Snipping Tool shortcut was turned off — " +
                        "re-enable it any time under Settings ▸ Bluetooth & devices ▸ Keyboard, or just pick a different hotkey here.",
                        Forms.ToolTipIcon.Info);
            }
            else
            {
                // Restore Snipping to its pre-dialog state, and honour any owed restore.
                bool owed = _settings.RestoreSnippingToolWhenReleased;
                if (snippingWasOn || owed) PrintScreenKey.SetSnipping(true);
                _settings.RestoreSnippingToolWhenReleased = false;
                SettingsStore.Save(_settings);

                if (owed && wasBarePrtSc)
                    _tray.ShowBalloonTip(4000, "iPrtSc",
                        "Print Screen released — Windows' Snipping Tool shortcut was restored.",
                        Forms.ToolTipIcon.Info);
            }
        }
        catch (Exception ex) { Logger.Log("FinalizePrintScreen", ex); }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var result = await UpdateChecker.CheckAsync(_settings);
            SettingsStore.Save(_settings); // persist throttle timestamp + cached latest version

            if (!result.UpdateAvailable || _updateAvailable) return;

            // Marshal UI changes (tray icon, tooltip) back onto the UI thread.
            await Dispatcher.InvokeAsync(() =>
            {
                _updateAvailable = true;
                _latestVersion = result.LatestVersion;
                _updateTimer?.Stop(); // badge stays until restart; no need to keep polling



                var old = _tray.Icon;
                _tray.Icon = IconFactory.CreateAppIcon(updateBadge: true);
                old?.Dispose();
                _tray.Text = TrayTooltip();

                _tray.ShowBalloonTip(4000, "iPrtSc",
                    $"Version {result.LatestVersion} is available. Right-click the tray icon to download.",
                    Forms.ToolTipIcon.Info);

                Logger.Log($"Update available: {result.LatestVersion} (current {UpdateChecker.Current}).");
            });
        }
        catch (Exception ex)
        {
            Logger.Log("CheckForUpdatesAsync", ex);
        }
    }

    private void OpenSettings()
    {
        // Release the global hotkeys while the dialog is open, otherwise pressing one
        // into a capture field fires the action instead of being recorded.
        _hotkey.UnregisterAll();

        // Disable the Snipping Tool Print Screen shortcut while Settings is open, otherwise pressing
        // Print Screen into the capture field would launch Snipping Tool over the dialog instead of
        // being recorded. FinalizePrintScreen settles the lasting state from the chosen hotkey.
        bool snippingWasOn = PrintScreenKey.SnippingEnabled();
        bool wasBarePrtSc = PrintScreenKey.IsBarePrintScreen(_settings);
        if (snippingWasOn) PrintScreenKey.SetSnipping(false);

        try
        {
            var working = _settings.Clone();
            var win = new SettingsWindow(working);
            if (win.ShowDialog() == true)
            {
                _settings = working;
                SettingsStore.Save(_settings);
                AutoStart.Apply(_settings.AutoStart);
            }
            FinalizePrintScreen(snippingWasOn, wasBarePrtSc, PrintScreenKey.IsBarePrintScreen(_settings));
        }
        catch (Exception ex)
        {
            Logger.Log("OpenSettings", ex);
        }
        finally
        {
            // Re-register whether saved or cancelled, so the hotkeys are always live again.
            bool ok = _hotkey.RegisterCapture(_settings);
            bool histOk = _hotkey.RegisterHistory(_settings);
            Logger.Log($"Settings closed. Re-register capture({_settings.HotkeyDisplay})={ok} history({_settings.HistoryHotkeyDisplay})={histOk}");
            _tray.Text = TrayTooltip();
            if (!ok) WarnHotkeyFailed(_settings.HotkeyDisplay);
            if (!histOk) WarnHotkeyFailed(_settings.HistoryHotkeyDisplay);
        }
    }

    private void OpenAbout()
    {
        try { new AboutWindow(_updateAvailable ? _latestVersion : null).ShowDialog(); }
        catch (Exception ex) { Logger.Log("OpenAbout", ex); }
    }

    private void BeginCapture()
    {
        if (_overlay != null)
        {
            Logger.Log("BeginCapture skipped: overlay already open.");
            return;
        }

        try
        {
            Logger.Log("Capturing virtual screen...");
            var cap = ScreenCapture.CaptureVirtualScreen();
            Logger.Log($"Captured {cap.bounds.Width}x{cap.bounds.Height} at ({cap.bounds.Left},{cap.bounds.Top}).");

            _overlay = new OverlayWindow(cap.bmp, cap.src, cap.bounds, _settings);
            _overlay.Saved += path =>
                _tray.ShowBalloonTip(2500, "iPrtSc", $"Saved: {path}", Forms.ToolTipIcon.Info);
            _overlay.Closed += (_, _) =>
            {
                cap.bmp.Dispose();
                _overlay = null;
                Logger.Log("Overlay closed.");
            };
            _overlay.Show();
            Logger.Log("Overlay shown.");
        }
        catch (Exception ex)
        {
            Logger.Log("BeginCapture", ex);
            _overlay = null;
        }
    }

    private void ExitApp()
    {
        Logger.Log("Exiting via tray.");
        _tray.Visible = false;
        _tray.Dispose();
        _hotkey.Dispose();
        _singleInstance?.ReleaseMutex();
        Shutdown();
    }
}

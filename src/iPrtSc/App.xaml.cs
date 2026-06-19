using System;
using System.Threading;
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
    private Mutex? _singleInstance;

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
            SetupHotkey();
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
        _hotkey.Pressed += OnHotkeyPressed;

        bool ok = _hotkey.Register(_settings);
        Logger.Log($"RegisterHotKey({_settings.HotkeyDisplay}) => {ok}");
        if (!ok)
        {
            _tray.ShowBalloonTip(3500, "iPrtSc",
                $"Could not register the hotkey \"{_settings.HotkeyDisplay}\" — it may be in use by another app.",
                Forms.ToolTipIcon.Warning);
        }
    }

    private void OnHotkeyPressed()
    {
        Logger.Log("Hotkey pressed.");
        BeginCapture();
    }

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = IconFactory.CreateAppIcon(),
            Visible = true,
            Text = $"iPrtSc — Capture: {_settings.HotkeyDisplay}"
        };

        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) BeginCapture();
            else if (e.Button == Forms.MouseButtons.Right) ShowTrayMenu();
        };
    }

    private void ShowTrayMenu()
    {
        _trayMenu?.Close();

        var menu = new TrayMenuWindow();
        menu.AddItem("Capture", _settings.HotkeyDisplay, BeginCapture);
        menu.AddItem("Settings…", "", OpenSettings);
        menu.AddItem("About", "", OpenAbout);
        menu.AddSeparator();
        menu.AddItem("Exit", "", ExitApp);
        menu.Closed += (_, _) => { if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null; };

        _trayMenu = menu;
        menu.ShowAtCursor();
    }

    private void OpenSettings()
    {
        // Release the global hotkey while the dialog is open, otherwise pressing it
        // into the capture field fires a capture instead of being recorded.
        _hotkey.Unregister();
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
        }
        catch (Exception ex)
        {
            Logger.Log("OpenSettings", ex);
        }
        finally
        {
            // Re-register whether saved or cancelled, so the hotkey is always live again.
            bool ok = _hotkey.Register(_settings);
            Logger.Log($"Settings closed. Re-register hotkey({_settings.HotkeyDisplay}) => {ok}");
            _tray.Text = $"iPrtSc — Capture: {_settings.HotkeyDisplay}";
            if (!ok)
            {
                _tray.ShowBalloonTip(3500, "iPrtSc",
                    $"Could not register the hotkey \"{_settings.HotkeyDisplay}\" — it may be in use by another app.",
                    Forms.ToolTipIcon.Warning);
            }
        }
    }

    private void OpenAbout()
    {
        try { new AboutWindow().ShowDialog(); }
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

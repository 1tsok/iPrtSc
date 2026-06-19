# iPrtSc
Windows 11+. C# + WPF (.NET 8).

## Features (current iteration — v0.2.5)
- Global hotkey (default **Home**) to trigger a screenshot.
- Full virtual desktop capture (all monitors), DPI-aware (PerMonitorV2).
- Modern dark overlay: spotlight dimming, accent selection border, size indicator.
- Floating Fluent toolbar: **Copy** (Enter), **Save** (Ctrl+S), **Cancel** (Esc).
- Tray icon with menu (screenshot / settings / exit).
- Windows autostart.
- Settings stored in `%AppData%\iPrtSc\settings.json`.

## Planned

## Build & Run
```powershell
dotnet build
dotnet run --project src/iPrtSc
\
```

## Hotkey Configuration
`HotkeyKey` — key name from `System.Windows.Forms.Keys` (`Home`, `PrintScreen`, `F9`…).  
`HotkeyModifiers` — `None` or a combination separated by commas: `Control,Alt,Shift,Win`.

> **Note:** `Home` without a modifier is captured globally, so in other applications
> this key will not function as intended while iPrtSc is running.
> This is configurable in `settings.json`.
```

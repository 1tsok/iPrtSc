# iPrtSc

[![Latest release](https://img.shields.io/github/v/release/1tsok/iPrtSc?sort=semver&cacheSeconds=3600)](https://github.com/1tsok/iPrtSc/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/1tsok/iPrtSc/total?cacheSeconds=3600)](https://github.com/1tsok/iPrtSc/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2011-0078D6)

A fast, lightweight screenshot tool for Windows 11. Press a hotkey, select an
area, annotate, then copy or save — all from the system tray.

## Screenshots
<img width="320" height="340" alt="image" src="https://github.com/user-attachments/assets/bb0e938d-7ac1-4245-a421-694d831c6c2a" />


## Features

- **Global hotkey** to start a capture (configurable; default **Home**).
- **Full multi-monitor capture**, DPI-aware (PerMonitor V2).
- **Adjustable selection** — drag the handles to resize, or drag inside to reposition.
- **Annotation tools** — pen, marker, line, arrow, rectangle, ellipse, text,
  numbered counter and blur, plus move and undo/redo.
- **Colour palette** — a 30-colour grid to recolour any annotation.
- **Copy or save** — quick-save or a save dialog, PNG or JPEG.
- **Screenshot history** with thumbnails in the tray, an optional hotkey, and
  automatic cleanup.
- **Update notifications** in the tray when a new version is available.
- **Print Screen support** on Windows 11 — reclaims the Print Screen key
  so it triggers iPrtSc.
- **Runs in the tray**, with optional autostart at sign-in.

## Install

Download the latest `iPrtSc-Setup-x.y.z.exe` from the
[Releases](https://github.com/1tsok/iPrtSc/releases) page and run it. It installs
per-user (no admin required) and bundles the .NET runtime.

## Build from source

```powershell
dotnet build
dotnet run --project src/iPrtSc
```

To produce the installer (requires Inno Setup 6):

```powershell
.\installer\build-installer.ps1
```

## Configuration

Settings live in `%AppData%\iPrtSc\settings.json` and are editable from the
in-app Settings window (tray → Settings).

- `HotkeyKey` — a key name from `System.Windows.Forms.Keys` (e.g. `Home`,
  `PrintScreen`, `F9`).
- `HotkeyModifiers` — `None`, or a comma-separated combination of
  `Control,Alt,Shift,Win`.
- `HistoryHotkeyKey` / `HistoryHotkeyModifiers` — optional hotkey that opens the
  History flyout; leave the key empty for none.

> A hotkey without a modifier (e.g. `Home`) is captured globally, so that key
> won't perform its normal function in other apps while iPrtSc is running.

## License

iPrtSc is free and open-source software, released under the
[MIT License](LICENSE).

Icons are from [Lucide](https://lucide.dev) (ISC License). See
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for full third-party notices.

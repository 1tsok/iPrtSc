# iPrtSc

[![Latest release](https://img.shields.io/github/v/release/1tsok/iPrtSc?sort=semver&cacheSeconds=3600)](https://github.com/1tsok/iPrtSc/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/1tsok/iPrtSc/total?cacheSeconds=3600)](https://github.com/1tsok/iPrtSc/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2011-0078D6)

A fast, lightweight screenshot tool for Windows 11. Press a hotkey, select an
area, annotate, grab text, then copy or save — all from the system tray.

## Screenshot

<img width="542" alt="iPrtSc capture overlay" src="docs/screenshot.png" />

## Features

- **Global hotkey** to start a capture (configurable; default **Home**),
  full multi-monitor and DPI-aware (PerMonitor V2).
- **Adjustable selection** — drag the handles to resize, or drag inside to
  reposition.
- **Annotation tools** — pen, marker, line, arrow, rectangle, ellipse, text,
  numbered steps and pixelate, plus move and undo/redo. Hold **Shift** with
  pen or marker to draw a straight line; the mouse wheel adjusts the brush,
  text and step size on the fly.
- **Colour palette** — a 30-colour grid to recolour any annotation.
- **Grab text (OCR)** — recognise text in the selection, drag across the words
  you want and press **Ctrl+C** to copy them. English and Ukrainian out of the
  box, mixed text supported; fully on-device (PP-OCRv5 on ONNX Runtime), no
  cloud involved.
- **Copy or save** — quick-save or a save dialog, PNG or JPEG.
- **Screenshot history** with thumbnails in the tray, an optional hotkey, and
  automatic cleanup.
- **Update notifications** in the tray when a new version is available.
- **Print Screen support** on Windows 11 — reclaims the Print Screen key
  so it triggers iPrtSc.
- **Runs in the tray**, with optional autostart at sign-in; dark, Fluent-style
  UI throughout.

## Install

Download the latest `iPrtSc-Setup-x.y.z.exe` from the
[Releases](https://github.com/1tsok/iPrtSc/releases) page and run it. It installs
per-user (no admin required) and bundles the .NET runtime — nothing else to
install.

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

Icons are from [Lucide](https://lucide.dev) (ISC License); text recognition
uses PaddleOCR PP-OCRv5 models on ONNX Runtime. See
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for full third-party notices.

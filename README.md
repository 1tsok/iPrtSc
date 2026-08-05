<p align="center">
  <img src="src/iPrtSc/Assets/icon-256.png" alt="iPrtSc logo" width="140"/>
</p>

<h1 align="center">iPrtSc</h1>

<p align="center">
  <b>A fast, lightweight screenshot tool for Windows 11.</b><br>
  <br>
  Press a hotkey, select an area, annotate, grab text,<br>
  then copy or save - all from the system tray.
</p>

<p align="center">
  <a href="https://github.com/1tsok/iPrtSc/releases/latest"><img src="https://img.shields.io/github/v/release/1tsok/iPrtSc?sort=semver&cacheSeconds=3600" alt="Latest release"/></a>
  <a href="https://github.com/1tsok/iPrtSc/releases"><img src="https://img.shields.io/github/downloads/1tsok/iPrtSc/total?cacheSeconds=3600" alt="Downloads"/></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT"/></a>
  <img src="https://img.shields.io/badge/platform-Windows%2011-0078D6" alt="Platform"/>
</p>

<p align="center">
  <a href="https://github.com/1tsok/iPrtSc/releases/latest">Download</a> ·
  <a href="https://github.com/1tsok/iPrtSc/releases">Releases</a> ·
  <a href="https://github.com/1tsok/iPrtSc/issues">Issues</a> ·
  <a href="THIRD_PARTY_NOTICES.md">Third-party notices</a>
</p>

<p align="center">
  <img src="https://github.com/user-attachments/assets/05c84097-9ce7-4ded-b118-c41be31fd852" alt="iPrtSc capture overlay with annotations" width="700"/>
</p>

---

### Why iPrtSc?

- **Capture and annotate in one flow** - one hotkey freezes the screen, drag a region, draw on it, press Enter. Nothing to open first.
- **Text recognition on device** - drag over words in the selection and copy them. English and Ukrainian, mixed text supported, no cloud involved.
- **Quick copy** - an optional hotkey that skips the tool bar entirely: release the mouse and the image is already on the clipboard.
- **Native and small** - C# and WPF, no browser engine inside. Lives in the tray, starts instantly.
- **Multi-monitor and DPI-aware** - PerMonitor V2, mixed-scaling setups included.
- **Free and open source** - MIT, no account, no telemetry, no upsell.

---

## Install

Download the latest `iPrtSc-Setup-x.y.z.exe` from [Releases](https://github.com/1tsok/iPrtSc/releases) and run it. It installs per-user (no admin required) and bundles the .NET runtime - nothing else to install.

---

## Quick Start

1. Launch iPrtSc - it appears in the system tray
2. Press `Home` (or `Print Screen`) to capture
3. Drag to select, annotate with the tool bar, press `Enter` to copy
4. Press `Esc` to cancel

---

<details>
<summary><b>All Features</b></summary>

### Capture
- **Global hotkey** - configurable, default `Home`. Freezes the screen so the shot never moves under you.
- **Print Screen support** - reclaims the Print Screen key on Windows 11 so it triggers iPrtSc instead of the built-in capture.
- **Quick copy** - optional hotkey that goes straight to the clipboard with no editing step.
- **Copy full screen** - optional hotkey that grabs the monitor under the cursor with no selection at all.
- **Adjustable selection** - drag the dot handles to resize, drag inside to reposition, `Ctrl+A` to take everything.
- **Multi-monitor and DPI-aware** - PerMonitor V2, correct on mixed-scaling setups.

### Annotation Tools
- **Pen and highlighter** - freeform strokes with smoothing; hold `Shift` for a straight line.
- **Shapes** - arrow, line, rectangle and ellipse; hold `Shift` for a square or circle.
- **Text** - click to type, click again to re-edit, with native text selection.
- **Numbered steps** - auto-incrementing markers for walkthroughs.
- **Stamps** - 14 rubber-print stamps (APPROVED, REJECTED, DRAFT, TOP SECRET, BUG, DONE, …) with light/dark/auto ink.
- **Pixelate** - blur out anything sensitive; the block pattern re-samples when you move it.
- **Color picker** - sample any pixel through a magnifier and copy its hex.
- **Color palette** - a 30-colour grid that recolours any annotation.
- **Move tool** - click an object to select it, then drag, resize, rotate, or `Del` to remove it.
- **Mouse wheel** - adjusts brush, text and step size on the fly.
- **Undo / redo** - `Ctrl+Z` and `Ctrl+Y`.

### Grab Text (OCR)
- Recognises text inside the selection, then drag across the words you want and press `Ctrl+C`.
- **English and Ukrainian** out of the box, mixed text supported.
- **Fully on device** - PP-OCRv5 models on ONNX Runtime, nothing leaves the machine.

### Output
- **Copy or save** - clipboard, quick-save to a default folder, or a save dialog.
- **PNG or JPEG**, with an optional "copy to clipboard when saving" mode.
- **History** - every capture is archived with thumbnails in the tray, an optional hotkey to open it, a pin to keep the flyout open, and automatic cleanup after a chosen period.

### Interface
- **Runs in the tray** with optional autostart at sign-in.
- **Dark, Fluent-style UI** throughout, with a configurable accent colour.
- **Update notifications** in the tray when a new version is available.

</details>

<details>
<summary><b>Keyboard Shortcuts</b></summary>

**Global** (configurable in Settings)

| Shortcut | Action |
|---|---|
| `Home` | Capture |
| `Print Screen` | Capture (when Print Screen support is on) |
| _unset_ | Quick copy - select and copy, no editing |
| _unset_ | Copy the monitor under the cursor |
| _unset_ | Open the History flyout |

**During a capture**

| Shortcut | Action |
|---|---|
| `Enter` | Copy to clipboard (copies the text in Grab text mode) |
| `Ctrl+S` | Save to file |
| `Ctrl+C` | Copy recognised text (Grab text mode) |
| `Ctrl+A` | Select everything / full screen |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo |
| `Del` | Delete the selected object |
| `Esc` | Deselect, then cancel the capture |
| `Shift` (while drawing) | Straight lines, squares and circles |
| Mouse wheel | Brush, text and step size |

**Tools** (available once a selection exists)

| Key | Tool |
|---|---|
| `C` | Select area |
| `V` | Move objects |
| `P` | Pen |
| `M` | Highlighter |
| `A` | Arrow |
| `L` | Line |
| `R` | Rectangle |
| `E` | Ellipse |
| `T` | Text |
| `N` | Numbered step |
| `S` | Stamps |
| `B` | Pixelate |
| `I` | Color picker |
| `G` | Grab text (OCR) |

</details>

<details>
<summary><b>Configuration</b></summary>

Settings live in `%AppData%\iPrtSc\settings.json` and are editable from the in-app Settings window (tray → Settings).

- `HotkeyKey` - a key name from `System.Windows.Forms.Keys` (e.g. `Home`, `PrintScreen`, `F9`).
- `HotkeyModifiers` - `None`, or a comma-separated combination of `Control,Alt,Shift,Win`.
- `QuickCopyHotkeyKey` / `QuickCopyHotkeyModifiers` - optional hotkey for a capture that goes straight to the clipboard; leave the key empty for none.
- `FullScreenHotkeyKey` / `FullScreenHotkeyModifiers` - optional hotkey that copies the monitor under the cursor; leave the key empty for none.
- `HistoryHotkeyKey` / `HistoryHotkeyModifiers` - optional hotkey that opens the History flyout; leave the key empty for none.

> A hotkey without a modifier (e.g. `Home`) is captured globally, so that key won't perform its normal function in other apps while iPrtSc is running.

</details>

<details>
<summary><b>Build from source</b></summary>

```powershell
dotnet build
dotnet run --project src/iPrtSc
```

To produce the installer (requires Inno Setup 6):

```powershell
.\installer\build-installer.ps1
```

</details>

---

## Requirements

Windows 11. The installer bundles the .NET runtime, so no separate install is needed.

## License

[MIT](LICENSE)

Icons are from [Lucide](https://lucide.dev) (ISC License); text recognition uses PaddleOCR PP-OCRv5 models on ONNX Runtime. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for full third-party notices.

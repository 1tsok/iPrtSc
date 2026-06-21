## What's new in 0.6.1

This is a small polish release focused on branding consistency and a clearer
update-available experience.

### Improvements
- **About window now uses the real app logo.** Replaced the hand-drawn vector
  brackets (which rendered slightly stretched) with the actual app icon, so the
  logo looks identical everywhere.
- **Tray icon is rendered from the app logo.** The tray now derives from the
  same source icon, recoloured white for contrast on both light and dark
  taskbars, with a subtle shadow.
- **Redesigned the "update available" badge.** The orange dot no longer clips at
  the icon edge and has a clean white ring, so it reads as a deliberate
  indicator instead of a stray pixel.

### Update notifications
- Added an **"Update available — Download"** banner inside the About window
  (shown only when a newer version exists) that links straight to the release.
- Added an **orange badge next to "About"** in the tray menu when an update is
  available.
- Removed the standalone "Update available — download…" tray-menu item; the
  badge + About banner replace it.

### Misc
- Added an **"iPrtSc on GitHub"** link in the About window.

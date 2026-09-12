## Modern Recycle Bin 1.1.0

Installer redesign.

### Changed

- **The installer is now DPI aware.** The previous build was not, so on a 125% or
  150% display Windows scaled the finished window bitmap and every glyph looked
  soft. Metrics and fonts are now scaled by the real DPI and text is rendered with
  ClearType.
- **The whole window is drawn with GDI+**: a borderless rounded frame, the two
  options as cards with custom checkboxes, a custom progress bar and a single
  call-to-action. The stock WinForms controls (group box, progress bar, flat
  button) that made it look dated are gone.
- Options can be toggled by clicking anywhere on their card, and the window can be
  dragged from any empty area and closed with Escape.
- The interface language, options and behaviour are unchanged, and the previous
  silent switches (`--silent`, `--no-shortcut`, `--no-takeover`) still work.

### Install

Download `ModernRecycleBinSetup.exe` below and run it — no administrator rights
needed. To remove it later, run `Uninstall.cmd` in the install folder; the desktop
Recycle Bin is restored to the system default automatically.
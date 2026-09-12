## Modern Recycle Bin 1.0.0

First public release — a Recycle Bin for Windows 11 rebuilt with **WebView2 + HTML/CSS**,
with the features the built-in one has never had.

### What's new

- **Restore anywhere** — pick any folder instead of only the original location
- **Copy out** — take a copy of a file while keeping the original in the bin
- **Image previews** — see a thumbnail in the details pane before restoring
- **Filter by type** and search over name, original path and type
- **Conflict handling** — overwrite, skip, or keep both
- **Windows 11 styling** — Fluent dark theme, 40 px rows, rounded hover states, smooth motion
- **Comfortable / compact density** and `Ctrl`+scroll zoom
- **Opens in about 300 ms** — window visible in ~100 ms

### Install

1. Download `ModernRecycleBinSetup.exe` below and run it — **no administrator rights needed**
2. Optionally create a desktop shortcut and let the desktop Recycle Bin open with this app
3. To remove it later, run `Uninstall.cmd` in the install folder; the system default is restored automatically

Prefer no installer? Grab `ModernRecycleBin-portable.zip` and just run `RecycleBin.exe`.

### Requirements

Windows 10 or 11, 64-bit. The WebView2 Runtime is preinstalled on Windows 11; if it is
missing the installer tells you and links to Microsoft's official download.

### Notes

- The binaries are not code-signed, so SmartScreen may ask you to confirm — choose **More info → Run anyway**, or build from source with `build.cmd`
- The interface is currently Simplified Chinese; English strings are planned
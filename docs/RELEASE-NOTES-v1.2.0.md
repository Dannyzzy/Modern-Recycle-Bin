## Modern Recycle Bin 1.2.0 — installing it is now one line

### New

**A one-click install path, built for networks where `github.com` does not
answer.** Paste this into PowerShell and it fetches the newest release and starts
it:

```powershell
irm https://ghfast.top/https://raw.githubusercontent.com/Dannyzzy/Modern-Recycle-Bin/main/Install-ModernRecycleBin.cmd -OutFile "$env:TEMP\mrb-install.cmd"; & "$env:TEMP\mrb-install.cmd"
```

What makes it worth having rather than just a download link:

- **It downloads through PowerShell, not `curl`.** An accelerator sets a Windows
  proxy that `curl` ignores — so "the browser opens GitHub, but the download times
  out at 0 bytes" is a real state on a real machine. Measured here, not guessed.
- **Direct first, then the [ghfast.top](https://ghfast.top) mirror**, so it works
  with or without an accelerator.
- **The download is size-checked.** A truncated transfer or an error page never
  reaches the installer pretending to be one.
- **It never fails quietly.** When both channels fail it prints every address it
  tried, plus the manual steps.

### Changed

- **The title bar now reads as two levels.** The app name and the version number
  used to sit in one flat grey line; the name is now brighter and the version
  recedes, so the eye lands on the brand first.
- Screenshots in the README are re-shot at this version — the published installer
  screenshot still said 1.0.0.

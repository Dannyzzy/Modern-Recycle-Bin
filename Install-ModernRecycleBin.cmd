@echo off
setlocal enabledelayedexpansion
rem ===============================================================
rem  Modern Recycle Bin - one-click installer
rem
rem  Downloads the newest release and starts it. Two things get in
rem  the way of a plain download, so both are handled here:
rem
rem   1. github.com may be unreachable - the download times out at
rem      0 bytes. The ghfast.top mirror is tried as a second channel.
rem   2. The Windows proxy (accelerators set one) is ignored by
rem      curl.exe, so a machine where the browser opens GitHub can
rem      still fail to download. PowerShell is used instead: .NET
rem      follows the system proxy, and connects directly when there
rem      is no proxy.
rem
rem  No administrator rights are needed, and nothing is installed
rem  until you confirm it in the installer window.
rem ===============================================================

set "REPO=Dannyzzy/Modern-Recycle-Bin"
set "ASSET=ModernRecycleBinSetup.exe"
set "DIRECT=https://github.com/%REPO%/releases/latest/download/%ASSET%"
set "MIRROR=https://ghfast.top/https://github.com/%REPO%/releases/latest/download/%ASSET%"
set "OUT=%TEMP%\%ASSET%"

echo.
echo ===============================================
echo   Modern Recycle Bin - one-click install
echo ===============================================
echo.

if exist "%OUT%" del /f /q "%OUT%" >nul 2>&1

call :fetch "github.com" "%DIRECT%"
if not defined OK call :fetch "the ghfast.top mirror" "%MIRROR%"

if not defined OK (
  echo.
  echo [x] Both download channels failed.
  echo       %DIRECT%
  echo       %MIRROR%
  echo.
  echo     What to do:
  echo       1. Run this script again - either channel can be slow once.
  echo       2. If you use an accelerator, turn it on - the system proxy
  echo          is followed automatically.
  echo       3. Or download by hand in a browser from
  echo          https://ghfast.top/https://github.com/%REPO%/releases/latest
  echo.
  pause
  exit /b 1
)

for %%A in ("%OUT%") do set "SIZE=%%~zA"
echo.
echo [ok] Saved %ASSET% ^(%SIZE% bytes^) to
echo      %OUT%
echo.
echo   Starting the installer now. When it finishes it shows what was
echo   detected on this machine - untick anything you do not want first.
echo.
pause
start "" "%OUT%"
exit /b 0

rem ---------------------------------------------------------------
rem  :fetch <label> <url>
rem  Sets OK=1 on success. A failed transfer can still leave a stub
rem  file behind, so the size is checked as well as the exit code.
rem ---------------------------------------------------------------
:fetch
echo [..] Downloading from %~1 ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; try { Invoke-WebRequest -Uri '%~2' -OutFile '%OUT%' -UseBasicParsing -TimeoutSec 180 } catch { Write-Host '      request failed - timeout, blocked, or DNS'; exit 1 }"
if errorlevel 1 (
  echo [x]  %~1 failed.
  if exist "%OUT%" del /f /q "%OUT%" >nul 2>&1
  exit /b 1
)
set "SZ=0"
for %%A in ("%OUT%") do set "SZ=%%~zA"
if !SZ! LSS 100000 (
  echo [x]  %~1 returned only !SZ! bytes - not the real installer.
  del /f /q "%OUT%" >nul 2>&1
  exit /b 1
)
echo [ok] %~1 succeeded ^(!SZ! bytes^).
set "OK=1"
exit /b 0
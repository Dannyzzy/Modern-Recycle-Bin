@echo off
setlocal enabledelayedexpansion
rem ===============================================================
rem  One-time setup so the project can be published to GitHub.
rem
rem  This script never asks you for a password, token or key. It
rem  only signs YOUR OWN account in on YOUR OWN machine.
rem ===============================================================

set "ROOT=%~dp0"
set "GH=%ROOT%tools\gh\bin\gh.exe"
set "PUBKEY=%USERPROFILE%\.ssh\id_ed25519.pub"

echo.
echo ===============================================
echo   GitHub setup for Modern Recycle Bin
echo ===============================================
echo.

echo [1/4] Checking github.com reachability...
curl -s -o NUL -m 12 https://github.com >nul 2>&1
if errorlevel 1 (
  echo.
  echo   [x] github.com is NOT reachable from this network right now.
  echo.
  echo   This is a network restriction, not a problem with the project.
  echo   To continue, do ONE of these:
  echo.
  echo     a^) Turn on your accelerator (UU / LeiShen) so github.com
  echo        becomes reachable, then run this script again.
  echo.
  echo     b^) Use any other way you have of reaching GitHub, then tell
  echo        the assistant to continue.
  echo.
  echo   Note: the assistant can already PUSH over SSH later even
  echo   without github.com, but signing in needs it once.
  echo.
  pause
  exit /b 1
)
echo       OK - github.com is reachable.

echo [2/4] Checking the GitHub CLI...
if not exist "%GH%" (
  echo       [x] gh.exe not found at %GH%
  pause
  exit /b 1
)
echo       OK.

echo [3/4] Signing in to GitHub...
echo.
echo       A browser window will open with a short code.
echo       Copy the code, paste it in the page, approve, come back.
echo.
pause
"%GH%" auth login --hostname github.com --git-protocol https --web

"%GH%" auth status
if errorlevel 1 (
  echo.
  echo   [x] Sign-in did not complete.
  pause
  exit /b 1
)

echo.
echo [4/4] Registering the SSH key so pushing works even when
echo       github.com is blocked...
if exist "%PUBKEY%" (
  "%GH%" ssh-key add "%PUBKEY%" --title "Modern Recycle Bin" 2>nul
  if errorlevel 1 (
    echo       (already registered, or could not add automatically)
  ) else (
    echo       SSH key registered.
  )
) else (
  echo       No SSH key found - skipping. HTTPS push will be used.
)

echo.
echo ===============================================
echo   Done. Tell the assistant: ????
echo ===============================================
echo.
pause
endlocal

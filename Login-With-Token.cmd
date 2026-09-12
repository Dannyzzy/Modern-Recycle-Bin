@echo off
setlocal
rem ===============================================================
rem  Sign in with a Personal Access Token - the path that works when
rem  github.com is blocked but api.github.com is reachable.
rem
rem  The token is read from the CLIPBOARD, so you never type it here
rem  and it is never displayed. The assistant never sees it either.
rem
rem  Create the token in your browser first:
rem    1. Open  https://github.com/settings/tokens/new
rem       (use a CLASSIC token - it can create repositories)
rem    2. Note        : Modern Recycle Bin
rem    3. Expiration  : 30 days is plenty
rem    4. Scopes      : tick  [x] repo
rem                          [x] workflow   (the repo contains a GitHub
rem                                          Actions workflow file)
rem    5. Generate token, then click the copy button
rem    6. Come back here and run this script
rem
rem  The token only ever goes to gh.exe on this machine.
rem ===============================================================

set "ROOT=%~dp0"
set "GH=%ROOT%tools\gh\bin\gh.exe"
set "PUBKEY=%USERPROFILE%\.ssh\id_ed25519.pub"

echo.
echo ===============================================
echo   GitHub sign-in from the clipboard
echo ===============================================
echo.
echo   First create a CLASSIC token at
echo     https://github.com/settings/tokens/new
echo   with scopes  repo  and  workflow,
echo   then use its copy button.
echo.
echo   This script reads the token straight from your clipboard.
echo   Nothing is typed here and nothing is displayed.
echo.
pause

if not exist "%GH%" (
  echo [x] gh.exe not found at %GH%
  pause
  exit /b 1
)

powershell -NoProfile -Command "$t = Get-Clipboard -Raw; if (-not $t) { Write-Host '[x] Clipboard is empty.'; exit 1 }; $t = $t.Trim(); if ($t.Length -lt 20) { Write-Host '[x] That does not look like a token.'; exit 1 }; $t | & '%GH%' auth login --hostname github.com --git-protocol https --with-token"
if errorlevel 1 (
  echo.
  echo [x] Sign-in failed. Check that the token has the repo and workflow scopes.
  pause
  exit /b 1
)

echo.
echo Signed in as:
"%GH%" api user --jq .login

echo.
echo Registering the SSH key so pushing works over the SSH channel...
if exist "%PUBKEY%" (
  "%GH%" ssh-key add "%PUBKEY%" --title "Modern Recycle Bin" 2>nul
  if errorlevel 1 ( echo       (already registered, or could not add automatically) ) else ( echo       SSH key registered. )
)

echo.
echo Clearing the clipboard so the token is not left behind...
powershell -NoProfile -Command "Set-Clipboard -Value ' '"

echo.
echo ===============================================
echo   Done. The assistant can now publish.
echo ===============================================
echo.
pause
endlocal
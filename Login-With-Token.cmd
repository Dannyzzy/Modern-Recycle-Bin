@echo off
setlocal
rem ===============================================================
rem  Fallback sign-in using a Personal Access Token.
rem
rem  Use this when the normal browser sign-in cannot reach
rem  github.com. You create the token yourself in the browser and
rem  paste it here - the assistant never sees it and never asks
rem  for it.
rem
rem  How to create the token (do this in your browser):
rem    1. Open https://github.com/settings/tokens?type=beta
rem    2. Generate new token  ->  name it anything
rem    3. Repository access: All repositories
rem    4. Permissions: Repository permissions ->
rem         Contents: Read and write
rem         Administration: Read and write   (needed to create the repo)
rem         Metadata: Read-only (auto)
rem    5. Generate, copy the token, then run this script and paste it.
rem ===============================================================

set "ROOT=%~dp0"
set "GH=%ROOT%tools\gh\bin\gh.exe"
set "PUBKEY=%USERPROFILE%\.ssh\id_ed25519.pub"

echo.
echo ===============================================
echo   GitHub sign-in with a Personal Access Token
echo ===============================================
echo.
echo   See the comments at the top of this file for how to
echo   create the token. It is pasted below and stays on this
echo   machine only.
echo.
pause

if not exist "%GH%" (
  echo [x] gh.exe not found at %GH%
  pause
  exit /b 1
)

echo Paste the token and press Enter:
"%GH%" auth login --hostname github.com --git-protocol https --with-token
if errorlevel 1 (
  echo.
  echo [x] That token was not accepted. Check the permissions and try again.
  pause
  exit /b 1
)

echo.
echo Signed in as:
"%GH%" api user --jq .login

echo.
echo Registering the SSH key so pushing works even when github.com is blocked...
if exist "%PUBKEY%" (
  "%GH%" ssh-key add "%PUBKEY%" --title "Modern Recycle Bin" 2>nul
  if errorlevel 1 ( echo       (already registered, or could not add automatically) ) else ( echo       SSH key registered. )
)

echo.
echo ===============================================
echo   Done. Tell the assistant: ????
echo ===============================================
echo.
pause
endlocal

@echo off
setlocal
rem ===============================================================
rem  One-time GitHub sign-in for publishing this project.
rem
rem  Opens a browser, shows a short code, and waits for you to
rem  approve. Nothing is typed here and no password or token is
rem  ever asked for by the assistant - this is your own account
rem  signing in on your own machine.
rem ===============================================================

set "GH=%~dp0tools\gh\bin\gh.exe"
if not exist "%GH%" (
  echo [x] gh.exe not found at:
  echo     %GH%
  echo     Re-run the assistant's download step first.
  pause
  exit /b 1
)

echo.
echo  GitHub sign-in
echo  --------------
echo  A browser window will open. Copy the code it shows, paste it
echo  into the page, and approve. Then come back here.
echo.
pause

"%GH%" auth login --hostname github.com --git-protocol https --web

echo.
echo  Result:
"%GH%" auth status
echo.
echo  If you see "Logged in to github.com", you are done -
echo  tell the assistant to continue.
echo.
pause
endlocal

@echo off
setlocal enabledelayedexpansion
rem ---------------------------------------------------------------
rem  Modern Recycle Bin - one-command build
rem  Needs nothing but the .NET Framework compiler shipped with
rem  Windows (C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe).
rem  This file must stay ASCII with CRLF line endings: cmd.exe parses
rem  batch files as ANSI and breaks on LF-only files.
rem ---------------------------------------------------------------

set "ROOT=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [x] csc.exe not found - .NET Framework 4.x is required.
  exit /b 1
)

echo.
echo [1/5] Compiling the application...
if not exist "%ROOT%dist" mkdir "%ROOT%dist"
if not exist "%ROOT%dist\ui" mkdir "%ROOT%dist\ui"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
  /out:"%ROOT%dist\RecycleBin.exe" ^
  /reference:System.dll /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll ^
  /reference:"%ROOT%lib\Microsoft.Web.WebView2.Core.dll" ^
  "%ROOT%src\RbWeb.cs"
if errorlevel 1 ( echo [x] compile failed & exit /b 1 )

echo [2/5] Assembling the portable build...
copy /y "%ROOT%lib\Microsoft.Web.WebView2.Core.dll" "%ROOT%dist\" >nul
copy /y "%ROOT%lib\WebView2Loader.dll" "%ROOT%dist\" >nul
copy /y "%ROOT%src\ui\index.html" "%ROOT%dist\ui\" >nul

echo [3/5] Packing the payload...
set "STAGE=%ROOT%build\payload"
if exist "%ROOT%build" rd /s /q "%ROOT%build%"
mkdir "%STAGE%\ui"
copy /y "%ROOT%dist\RecycleBin.exe" "%STAGE%\" >nul
copy /y "%ROOT%dist\Microsoft.Web.WebView2.Core.dll" "%STAGE%\" >nul
copy /y "%ROOT%dist\WebView2Loader.dll" "%STAGE%\" >nul
copy /y "%ROOT%src\ui\index.html" "%STAGE%\ui\" >nul
copy /y "%ROOT%lib\WebView2-LICENSE.txt" "%STAGE%\" >nul
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%ROOT%build\payload.zip' -Force"
if errorlevel 1 ( echo [x] packing failed & exit /b 1 )

echo [4/5] Building the single-file installer...
rem csc does not split "path,ID" when the whole argument is quoted, so run from
rem the build folder and pass a bare file name.
pushd "%ROOT%build"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
  /out:"%ROOT%dist\ModernRecycleBinSetup.exe" ^
  /reference:System.dll /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ^
  /resource:payload.zip,PAYLOAD ^
  "%ROOT%installer\Setup.cs"
set "RC=%errorlevel%"
popd
if not "%RC%"=="0" ( echo [x] installer build failed & exit /b 1 )

echo [5/5] Building the portable zip...
set "PORTABLE=%ROOT%build\portable\ModernRecycleBin"
mkdir "%PORTABLE%\ui"
copy /y "%ROOT%dist\RecycleBin.exe" "%PORTABLE%\" >nul
copy /y "%ROOT%dist\Microsoft.Web.WebView2.Core.dll" "%PORTABLE%\" >nul
copy /y "%ROOT%dist\WebView2Loader.dll" "%PORTABLE%\" >nul
copy /y "%ROOT%src\ui\index.html" "%PORTABLE%\ui\" >nul
copy /y "%ROOT%lib\WebView2-LICENSE.txt" "%PORTABLE%\" >nul
copy /y "%ROOT%LICENSE" "%PORTABLE%\LICENSE.txt" >nul
powershell -NoProfile -Command "Set-Content -Path '%PORTABLE%\README.txt' -Encoding UTF8 -Value @('Modern Recycle Bin (portable)','','Run RecycleBin.exe directly - no install, no admin rights, no registry changes.','Delete this folder to remove it completely.','','Requires the WebView2 Runtime (built into Windows 11). If missing:','https://go.microsoft.com/fwlink/p/?LinkId=2124703')"
powershell -NoProfile -Command "if (Test-Path '%ROOT%dist\ModernRecycleBin-portable.zip') { Remove-Item '%ROOT%dist\ModernRecycleBin-portable.zip' -Force }; Compress-Archive -Path '%ROOT%build\portable\*' -DestinationPath '%ROOT%dist\ModernRecycleBin-portable.zip' -Force"
if errorlevel 1 ( echo [x] portable zip failed & exit /b 1 )

echo.
echo [OK] Build finished:
echo      dist\RecycleBin.exe                  portable application
echo      dist\ModernRecycleBinSetup.exe       single-file installer
echo      dist\ModernRecycleBin-portable.zip   portable zip
echo.
endlocal
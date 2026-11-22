@echo off
setlocal
cd /d "%~dp0"

rem  build.bat          build, then start Moon Clock
rem  build.bat --check  only build (used by the GitHub check; never closes or starts anything)
set "CHECK="
if /i "%~1"=="--check" set "CHECK=1"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo Couldn't find the C# compiler that comes with Windows ^(.NET Framework 4^).
  if not defined CHECK pause
  exit /b 1
)
set "CSC=%FW%\csc.exe"
set "REFS=/r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll /lib:"%FW%\WPF" /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:WindowsBase.dll"

if defined CHECK goto build

rem Ask the running clock to close so it can restore icons and the taskbar, then force it if needed
tasklist /fi "imagename eq MoonClock.exe" | find /i "MoonClock.exe" >nul && (
  taskkill /im MoonClock.exe >nul 2>&1
  timeout /t 2 >nul
  taskkill /im MoonClock.exe /f >nul 2>&1
)

:build
rem Remove the old copy first, so a failed build can never look like a successful one
if exist MoonClock.exe del /q MoonClock.exe >nul 2>&1
if exist MoonClock.exe (
  echo MoonClock.exe is still running or locked. Quit it from the tray and try again.
  if not defined CHECK pause
  exit /b 1
)
echo Building Moon Clock...
rem "What's playing" is read from Windows at run time, so there's only one build and it always includes it.
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:moon.ico /out:MoonClock.exe %REFS% MoonClock.cs Sky.cs Taskbar.cs
if errorlevel 1 (
  echo.
  echo The build failed. The error is shown above. Please open an issue with a screenshot:
  echo https://github.com/mayank-singh-io/Nightfall-Desktop/issues
  if not defined CHECK pause
  exit /b 1
)
if defined CHECK exit /b 0

echo.
echo Done! Starting Moon Clock...
start "" "%~dp0MoonClock.exe"
timeout /t 3 >nul

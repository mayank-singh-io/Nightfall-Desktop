@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo Couldn't find the C# compiler that comes with Windows.
  pause
  exit /b 1
)
set "CSC=%FW%\csc.exe"
set "REFS=/r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll /lib:"%FW%\WPF" /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:WindowsBase.dll"

rem Windows Runtime references, needed for "what's playing"
set "WINRT="
for %%f in ("%WINDIR%\System32\WinMetadata\*.winmd") do set "WINRT=!WINRT! /r:"%%f""
for %%d in (System.Runtime.dll System.Runtime.WindowsRuntime.dll System.Runtime.InteropServices.WindowsRuntime.dll System.Threading.Tasks.dll) do (
  if exist "%FW%\%%d" set "WINRT=!WINRT! /r:"%FW%\%%d""
)

rem Ask the running clock to close so it can restore icons and the taskbar, then force it if needed
tasklist /fi "imagename eq MoonClock.exe" | find /i "MoonClock.exe" >nul && (
  taskkill /im MoonClock.exe >nul 2>&1
  timeout /t 2 >nul
  taskkill /im MoonClock.exe /f >nul 2>&1
)

echo Building Moon Clock with "what's playing"...
"%CSC%" /nologo /target:winexe /optimize+ /define:MEDIA /win32icon:moon.ico /out:MoonClock.exe %REFS% !WINRT! MoonClock.cs Sky.cs Taskbar.cs
if errorlevel 1 (
  echo.
  echo That version didn't build on this PC. Building without "what's playing" instead...
  "%CSC%" /nologo /target:winexe /optimize+ /win32icon:moon.ico /out:MoonClock.exe %REFS% MoonClock.cs Sky.cs Taskbar.cs
  if errorlevel 1 (
    echo.
    echo The build failed. Take a screenshot of this window and send it to Claude.
    pause
    exit /b 1
  )
)

echo.
echo Done! Starting Moon Clock...
start "" "%~dp0MoonClock.exe"
timeout /t 3 >nul

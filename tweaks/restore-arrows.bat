@echo off
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons" /v 29 /f >nul 2>&1
echo Refreshing icons, your screen will flicker for a moment...
taskkill /f /im explorer.exe >nul 2>&1
timeout /t 1 >nul
del /a /q "%LOCALAPPDATA%\IconCache.db" >nul 2>&1
del /a /q "%LOCALAPPDATA%\Microsoft\Windows\Explorer\iconcache*.db" >nul 2>&1
ie4uinit.exe -show
start explorer.exe
echo.
echo Shortcut arrows restored.
pause

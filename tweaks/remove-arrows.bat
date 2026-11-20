@echo off
:: Ask for admin rights (the setting lives in a system-wide part of the registry)
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
:: Use the blank icon that ships inside Windows itself
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons" /v 29 /t REG_SZ /d "%SystemRoot%\System32\imageres.dll,-17" /f >nul
del /q "C:\Icons\blank.ico" >nul 2>&1
call :refresh
echo.
echo Shortcut arrows removed.
echo If you still see black squares, restart your PC once.
pause
exit /b

:refresh
echo Refreshing icons, your screen will flicker for a moment...
taskkill /f /im explorer.exe >nul 2>&1
timeout /t 1 >nul
del /a /q "%LOCALAPPDATA%\IconCache.db" >nul 2>&1
del /a /q "%LOCALAPPDATA%\Microsoft\Windows\Explorer\iconcache*.db" >nul 2>&1
del /a /q "%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache*.db" >nul 2>&1
ie4uinit.exe -show
start explorer.exe
exit /b

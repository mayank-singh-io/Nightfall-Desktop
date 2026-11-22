# Nightfall Desktop uninstaller
# Run in PowerShell:
#   irm https://raw.githubusercontent.com/mayank-singh-io/Nightfall-Desktop/main/uninstall.ps1 | iex

& {
    $Targets = @('C:\Moon Clock', 'C:\Nightfall Tab', 'C:\Nightfall Theme')

    function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }
    function Ask([string]$q, [bool]$default = $true) {
        $hint = if ($default) { '[Y/n]' } else { '[y/N]' }
        $a = Read-Host "$q $hint"
        if ($a -eq '') { return $default }
        return ($a -match '^(y|yes)$')
    }

    Say "`n  Nightfall Desktop: uninstall`n" Magenta

    # Close Moon Clock politely first, so it puts your desktop icons and taskbar back
    $clock = Get-Process -Name 'MoonClock' -ErrorAction SilentlyContinue
    if ($clock) {
        Say "Closing Moon Clock..." Cyan
        & taskkill /im MoonClock.exe 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        $stuck = Get-Process -Name 'MoonClock' -ErrorAction SilentlyContinue
        if ($stuck) {
            $stuck | Stop-Process -Force
            # it couldn't tidy up, so restart Explorer to bring back desktop icons and the normal taskbar
            Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
            if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer }
        }
    }
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'NightfallMoonClock' -ErrorAction SilentlyContinue

    foreach ($t in $Targets) {
        if ((Test-Path $t) -and (Ask "Delete $t ?")) {
            Remove-Item -Path $t -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path $t) { Say "  Couldn't delete everything in $t (a file may be in use)." Yellow } else { Say "  Deleted." Green }
        }
    }

    $settings = Join-Path $env:APPDATA 'NightfallMoonClock'
    if ((Test-Path $settings) -and (Ask "Delete Moon Clock's saved settings?")) {
        Remove-Item -Path $settings -Recurse -Force -ErrorAction SilentlyContinue
        Say "  Deleted." Green
    }

    $arrows = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons' -Name '29' -ErrorAction SilentlyContinue
    if ($arrows -and (Ask "Bring back the shortcut arrows on desktop icons? (asks for admin)")) {
        $cmd = "reg delete 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons' /v 29 /f; " +
               "Stop-Process -Name explorer -Force; Start-Sleep 1; ie4uinit.exe -show; if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer }"
        Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile', '-Command', $cmd -Wait
        Say "  Shortcut arrows restored." Green
    }

    Say "`nLast step: in your browser, open brave://extensions (or chrome://extensions) and click Remove on Nightfall Tab and Nightfall Theme." Yellow
    Say "Done." Magenta
}

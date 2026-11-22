# Nightfall Desktop installer
# Run in PowerShell:
#   irm https://raw.githubusercontent.com/mayank-singh-io/Nightfall-Desktop/main/install.ps1 | iex

& {
    $ErrorActionPreference = 'Stop'
    $RepoName = 'mayank-singh-io/Nightfall-Desktop'
    $Branch   = 'main'
    $Targets  = @{
        MoonClock = 'C:\Moon Clock'
        Tab       = 'C:\Nightfall Tab'
        Theme     = 'C:\Nightfall Theme'
    }

    function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }
    function Ask([string]$q, [bool]$default = $true) {
        $hint = if ($default) { '[Y/n]' } else { '[y/N]' }
        $a = Read-Host "$q $hint"
        if ($a -eq '') { return $default }
        return ($a -match '^(y|yes)$')
    }
    function Copy-Folder([string]$from, [string]$to) {
        New-Item -ItemType Directory -Path $to -Force | Out-Null
        & robocopy $from $to /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Couldn't copy files to $to" }
    }

    Say ""
    Say "  Nightfall Desktop v1.0.0" Magenta
    Say "  A calm night-sky setup for Windows 10`n" DarkGray

    if ([Environment]::OSVersion.Version.Major -lt 10) { Say "This needs Windows 10 or later." Red; return }
    $build = 0
    try { $build = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber } catch { }
    $isWin11 = $build -ge 22000
    if ($isWin11) { Say "Note: you're on Windows 11. Everything works except taskbar centering and transparency, which are Windows 10 only.`n" Yellow }

    # ---------- what to install
    $doClock  = Ask "Install Moon Clock (desktop clock, away-mode sky, taskbar centering and transparency)?"
    $doStart  = if ($doClock) { Ask "  Start Moon Clock automatically with Windows?" } else { $false }
    $doTaskbar = if ($doClock -and -not $isWin11) { Ask "  Let Moon Clock center your taskbar icons and make the taskbar clear?" } else { $false }
    $doTab    = Ask "Install Nightfall Tab (new tab page for Brave or Chrome)?"
    $doTheme  = Ask "Install Nightfall Theme (matching browser colors)?"
    $doArrows = Ask "Remove shortcut arrows from desktop icons? (asks for admin)" $false
    if (-not ($doClock -or $doTab -or $doTheme -or $doArrows)) { Say "`nNothing selected. Bye!" ; return }

    # ---------- tools that would fight over the taskbar
    if ($doClock) {
        $rivals = Get-Process -Name 'TaskbarX', 'TranslucentTB' -ErrorAction SilentlyContinue
        if ($rivals) {
            Say "`nTaskbarX or TranslucentTB is running. Moon Clock does the same jobs, and running both makes them fight." Yellow
            if (Ask "Close them now?") { $rivals | Stop-Process -Force; Say "  Closed. You can uninstall them later." Green }
        }
    }

    # ---------- download
    Say "`nDownloading from GitHub..." Cyan
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $work = Join-Path $env:TEMP ("nightfall-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $zip = Join-Path $work 'repo.zip'
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/$RepoName/archive/refs/heads/$Branch.zip" -OutFile $zip
        Expand-Archive -Path $zip -DestinationPath $work -Force
    } catch {
        Say "Download failed: $($_.Exception.Message)" Red
        Say "Check your internet connection and try again." Yellow
        return
    }
    $src = Get-ChildItem -Path $work -Directory | Select-Object -First 1
    if (-not $src) { Say "The download looks empty." Red; return }
    $src = $src.FullName

    try {
        # ---------- Moon Clock
        if ($doClock) {
            Say "`nMoon Clock" Cyan
            Copy-Folder (Join-Path $src 'moon-clock') $Targets.MoonClock
            $settingsDir = Join-Path $env:APPDATA 'NightfallMoonClock'
            $settingsFile = Join-Path $settingsDir 'settings.ini'
            if (-not $doTaskbar -and -not (Test-Path $settingsFile)) {
                New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
                Set-Content -Path $settingsFile -Value @('CenterTaskbar=false', 'TaskbarStyle=off', 'TaskbarMaximized=same') -Encoding ASCII
            }
            Say "  Copied to $($Targets.MoonClock). Building (takes a few seconds)..." Gray
            $started = Get-Date
            $proc = Start-Process -FilePath (Join-Path $Targets.MoonClock 'build.bat') -WorkingDirectory $Targets.MoonClock -PassThru
            $proc.WaitForExit(120000) | Out-Null
            $exe = Join-Path $Targets.MoonClock 'MoonClock.exe'
            if ((Test-Path $exe) -and ((Get-Item $exe).LastWriteTime -ge $started.AddSeconds(-5))) {
                Say "  Built and started. Look for the moon in your system tray." Green
                if ($doStart) {
                    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'NightfallMoonClock' -Value "`"$exe`""
                    Say "  Set to start with Windows." Green
                }
            } else {
                Say "  The build didn't produce MoonClock.exe. Run build.bat in $($Targets.MoonClock) to see the error." Yellow
            }
        }

        # ---------- browser extensions
        $loaded = @()
        if ($doTab)   { Copy-Folder (Join-Path $src 'nightfall-tab')   $Targets.Tab;   $loaded += $Targets.Tab;   Say "`nNightfall Tab copied to $($Targets.Tab)" Green }
        if ($doTheme) { Copy-Folder (Join-Path $src 'nightfall-theme') $Targets.Theme; $loaded += $Targets.Theme; Say "Nightfall Theme copied to $($Targets.Theme)" Green }
        if ($loaded.Count -gt 0) {
            Say "`nBrowsers only allow you to add these by hand (a security rule). It takes a minute:" Yellow
            Say "  1. A brave://extensions page will open (in Chrome, go to chrome://extensions)." Yellow
            Say "  2. Turn on Developer mode (top right)." Yellow
            Say "  3. Click Load unpacked and pick each folder:" Yellow
            foreach ($f in $loaded) { Say "       $f" White }
            try { Start-Process 'brave' 'brave://extensions' } catch { }
        }

        # ---------- shortcut arrows
        if ($doArrows) {
            Say "`nRemoving shortcut arrows (approve the admin prompt)..." Cyan
            $tweak = Join-Path $env:TEMP 'nightfall-remove-arrows.bat'
            Copy-Item (Join-Path $src 'tweaks\remove-arrows.bat') $tweak -Force
            Start-Process -FilePath $tweak -Wait
        }
    }
    catch {
        Say "`nSomething went wrong: $($_.Exception.Message)" Red
    }
    finally {
        Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    Say "`nAll done! Enjoy the night sky." Magenta
    if ($doClock) {
        Say "Tip: right-click the moon in your system tray for settings, or choose 'Show the night sky now'." DarkGray
        Say "Away mode starts on: icons hide after 3 idle minutes, the sky appears after 10, and your PC locks 20 minutes after that." DarkGray
        Say "Change or turn any of these off in the tray menu > Away mode settings." DarkGray
    }
    Say "To remove everything later:  irm https://raw.githubusercontent.com/$RepoName/$Branch/uninstall.ps1 | iex" DarkGray
}

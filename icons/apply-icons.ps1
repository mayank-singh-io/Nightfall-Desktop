<#
  apply-icons.ps1
  Applies your custom icons to desktop shortcuts, Start menu shortcuts, taskbar pins,
  desktop folders, This PC, Recycle Bin and Control Panel.
  Run it with apply-icons.bat (it asks for admin rights).
  Undo everything with undo-icons.bat.
#>
param([switch]$Undo)

$IconDir    = 'C:\Icons'
$BackupFile = Join-Path $IconDir 'icon-backup.json'

# ---------- Ask for admin rights (needed for Program Files and all-users Start menu) ----------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Undo) { $argList += ' -Undo' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

# ---------- Which icon goes where (edit these if you want different matches) ----------
# Shortcuts are matched by name. The first matching pattern wins.
$ShortcutMap = [ordered]@{
    'Google Chrome*'      = 'browser-globe'
    'Firefox*'            = 'firefox'
    'Mozilla Firefox*'    = 'firefox'
    'Brave*'              = 'brave'
    'YouTube Music*'      = 'music'
    'Smart*Sched*'        = 'smart-scheduler-app'
    'Nexus*'              = 'nexus-app'
    'Visual Studio Code*' = 'vscode'
    'Termius*'            = 'termius'
    'Ollama*'             = 'ollama'
    'Tailscale*'          = 'private-network'
    'WhatsApp*'           = 'messages'
    'Word'                = 'document'
    'Microsoft Word*'     = 'document'
    'Excel'               = 'spreadsheet'
    'Microsoft Excel*'    = 'spreadsheet'
    'Control Panel*'      = 'control-panel'
    'This PC*'            = 'this-pc'
}

# Folders on your desktop
$FolderMap = [ordered]@{
    'Nexus blueprints'   = 'nexus-blueprints'
    'Nexus installation' = 'nexus-installation'
    '.md files'          = 'md-files'
    'Scheduler'          = 'scheduler'
    'Project Guides'     = 'project-guides'
}

# Special desktop icons (registry)
$ClsidMap = [ordered]@{
    '{645FF040-5081-101B-9F08-00AA002F954E}' = [ordered]@{ '(default)' = 'recycle-bin-empty'; 'Empty' = 'recycle-bin-empty'; 'Full' = 'recycle-bin-full' }
    '{20D04FE0-3AEA-1069-A2D8-08002B30309D}' = [ordered]@{ '(default)' = 'this-pc' }
    '{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}' = [ordered]@{ '(default)' = 'control-panel' }
}

# Files that force an app's own Start tile logo
$Manifests = @(
    "$env:ProgramFiles\Mozilla Firefox\firefox.VisualElementsManifest.xml",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.VisualElementsManifest.xml",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.VisualElementsManifest.xml",
    "$env:ProgramFiles\BraveSoftware\Brave-Browser\Application\brave.VisualElementsManifest.xml",
    "$env:LOCALAPPDATA\Programs\Microsoft VS Code\Code.VisualElementsManifest.xml",
    "$env:ProgramFiles\Microsoft VS Code\Code.VisualElementsManifest.xml",
    "$env:ProgramFiles\Microsoft Office\root\Office16\WINWORD.VisualElementsManifest.xml",
    "$env:ProgramFiles\Microsoft Office\root\Office16\EXCEL.VisualElementsManifest.xml",
    "${env:ProgramFiles(x86)}\Microsoft Office\root\Office16\WINWORD.VisualElementsManifest.xml",
    "${env:ProgramFiles(x86)}\Microsoft Office\root\Office16\EXCEL.VisualElementsManifest.xml"
)

# Start tile background color (shows around the icon on tiles)
$TileColor = '#2b2b2b'

# Places to look for shortcuts
$Desktop   = [Environment]::GetFolderPath('Desktop')
$Locations = [ordered]@{
    'Desktop shortcuts'                  = $Desktop
    'Public desktop shortcuts'           = [Environment]::GetFolderPath('CommonDesktopDirectory')
    'Start menu (shell:programs)'        = [Environment]::GetFolderPath('Programs')
    'Start menu (shell:common programs)' = [Environment]::GetFolderPath('CommonPrograms')
    'Taskbar pins'                       = "$env:APPDATA\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"
}

# ---------- Helpers ----------
function Get-IconPath([string]$name) { Join-Path $IconDir "$name.ico" }

function Refresh-Icons {
    Write-Host "`nRefreshing icon cache..." -ForegroundColor Cyan
    Start-Process ie4uinit.exe -ArgumentList '-show' -Wait -ErrorAction SilentlyContinue
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
}


function Set-TileManifest([string]$exe, [string]$iconName) {
    $p150 = Join-Path $IconDir "tiles\$iconName-150.png"
    $p70  = Join-Path $IconDir "tiles\$iconName-70.png"
    if (-not (Test-Path $p150) -or -not (Test-Path $p70)) { return }
    $dir  = Split-Path $exe
    $base = [IO.Path]::GetFileNameWithoutExtension($exe)
    $man  = Join-Path $dir "$base.VisualElementsManifest.xml"
    $tileDir = Join-Path $dir "CustomTile\$base"
    try {
        if (Test-Path -LiteralPath $man) {
            $content = Get-Content -LiteralPath $man -Raw
            if ($content -notmatch 'CustomTile') {
                if (Test-Path -LiteralPath "$man.bak") { Remove-Item -LiteralPath "$man.bak" -Force }
                Rename-Item -LiteralPath $man -NewName ((Split-Path $man -Leaf) + '.bak') -ErrorAction Stop
                if (-not (Test-Backed $backup.manifests $man $null)) { [void]$backup.manifests.Add($man) }
            }
        }
        New-Item -ItemType Directory -Path $tileDir -Force | Out-Null
        Copy-Item $p150 (Join-Path $tileDir '150.png') -Force
        Copy-Item $p70  (Join-Path $tileDir '70.png')  -Force
        $xml = "<Application xmlns:xsi=`"http://www.w3.org/2001/XMLSchema-instance`">`r`n" +
               "  <VisualElements ShowNameOnSquare150x150Logo=`"on`" Square150x150Logo=`"CustomTile\$base\150.png`" Square70x70Logo=`"CustomTile\$base\70.png`" ForegroundText=`"light`" BackgroundColor=`"$TileColor`"/>`r`n" +
               "</Application>"
        Set-Content -LiteralPath $man -Value $xml -Encoding UTF8
        if (-not (Test-Backed $backup.tiles $man $null)) {
            [void]$backup.tiles.Add([pscustomobject]@{ Path = $man; Dir = $tileDir })
        }
        Write-Host "      + big Start tile for $base" -ForegroundColor DarkGreen
    } catch {
        Write-Host "      (could not set Start tile for $base)" -ForegroundColor Yellow
    }
}

$shell = New-Object -ComObject WScript.Shell

# =====================================================================
#  UNDO
# =====================================================================
if ($Undo) {
    if (-not (Test-Path $BackupFile)) {
        Write-Host "No backup found at $BackupFile - nothing to undo." -ForegroundColor Yellow
        Read-Host 'Press Enter to close'; exit
    }
    $b = Get-Content $BackupFile -Raw | ConvertFrom-Json

    foreach ($s in @($b.shortcuts)) {
        if ($s -and (Test-Path -LiteralPath $s.Path)) {
            try {
                $lnk = $shell.CreateShortcut($s.Path)
                $lnk.IconLocation = [string]$s.Old
                $lnk.Save()
                Write-Host "  [restored] $($s.Path)" -ForegroundColor Green
            } catch { Write-Host "  [skipped] $($s.Path)" -ForegroundColor Yellow }
        }
    }

    foreach ($f in @($b.folders)) {
        if ($f -and (Test-Path -LiteralPath $f.Path)) {
            $ini = Join-Path $f.Path 'desktop.ini'
            if (Test-Path -LiteralPath $ini) { attrib -h -s "$ini" | Out-Null }
            if ($f.OldIni) {
                Set-Content -LiteralPath $ini -Value $f.OldIni -Encoding Unicode
                attrib +h +s "$ini" | Out-Null
            } else {
                Remove-Item -LiteralPath $ini -Force -ErrorAction SilentlyContinue
                attrib -r "$($f.Path)" | Out-Null
            }
            Write-Host "  [restored] $($f.Path)" -ForegroundColor Green
        }
    }

    foreach ($t in @($b.tiles)) {
        if (-not $t) { continue }
        if (Test-Path -LiteralPath $t.Path) {
            $c = Get-Content -LiteralPath $t.Path -Raw
            if ($c -match 'CustomTile') { Remove-Item -LiteralPath $t.Path -Force -ErrorAction SilentlyContinue }
        }
        if (Test-Path -LiteralPath $t.Dir) { Remove-Item -LiteralPath $t.Dir -Recurse -Force -ErrorAction SilentlyContinue }
        $parent = Split-Path $t.Dir
        if ((Split-Path $parent -Leaf) -eq 'CustomTile' -and -not (Get-ChildItem -LiteralPath $parent -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $parent -Force -ErrorAction SilentlyContinue
        }
        Write-Host "  [restored tile] $($t.Path)" -ForegroundColor Green
    }

    foreach ($m in @($b.manifests)) {
        if ($m -and (Test-Path -LiteralPath "$m.bak") -and -not (Test-Path -LiteralPath $m)) {
            Rename-Item -LiteralPath "$m.bak" -NewName (Split-Path $m -Leaf)
            Write-Host "  [restored] $m" -ForegroundColor Green
        }
    }

    foreach ($r in @($b.registry)) {
        if (-not $r) { continue }
        if ($null -eq $r.Old -or $r.Old -eq '') {
            Remove-ItemProperty -Path $r.Path -Name $r.Name -ErrorAction SilentlyContinue
        } else {
            Set-ItemProperty -Path $r.Path -Name $r.Name -Value $r.Old
        }
        Write-Host "  [restored] $($r.Path) $($r.Name)" -ForegroundColor Green
    }

    Rename-Item -LiteralPath $BackupFile -NewName ("icon-backup-undone-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))
    Refresh-Icons
    Write-Host "`nAll done - original icons restored." -ForegroundColor Cyan
    Read-Host 'Press Enter to close'; exit
}

# =====================================================================
#  APPLY
# =====================================================================

# 1. Copy icons to C:\Icons
New-Item -ItemType Directory -Path $IconDir -Force | Out-Null
if ($PSScriptRoot -and ($PSScriptRoot.TrimEnd('\') -ne $IconDir)) {
    $iconSrc = if (Test-Path (Join-Path $PSScriptRoot 'icons')) { Join-Path $PSScriptRoot 'icons' } else { $PSScriptRoot }
    Copy-Item -Path (Join-Path $iconSrc '*.ico') -Destination $IconDir -Force -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $PSScriptRoot 'tiles')) {
        Copy-Item -Path (Join-Path $PSScriptRoot 'tiles') -Destination $IconDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
$count = @(Get-ChildItem $IconDir -Filter *.ico).Count
Write-Host "Icons in $IconDir : $count" -ForegroundColor Cyan
if ($count -eq 0) {
    Write-Host 'No .ico files found. Put this script in the same folder as your icons.' -ForegroundColor Red
    Read-Host 'Press Enter to close'; exit
}

# Load existing backup so originals are never overwritten on re-runs
$backup = [ordered]@{
    shortcuts = New-Object System.Collections.ArrayList
    folders   = New-Object System.Collections.ArrayList
    manifests = New-Object System.Collections.ArrayList
    registry  = New-Object System.Collections.ArrayList
    tiles     = New-Object System.Collections.ArrayList
}
if (Test-Path $BackupFile) {
    $old = Get-Content $BackupFile -Raw | ConvertFrom-Json
    foreach ($k in 'shortcuts', 'folders', 'manifests', 'registry', 'tiles') {
        foreach ($e in @($old.$k)) { if ($e) { [void]$backup[$k].Add($e) } }
    }
}
function Test-Backed($list, $path, $name) {
    foreach ($e in $list) {
        if ($e -is [string]) { if ($e -eq $path) { return $true } }
        elseif ($e.Path -eq $path -and (-not $name -or $e.Name -eq $name)) { return $true }
    }
    return $false
}

# 2. Start-tile manifests (so tiles use your icons)
Write-Host "`nStart tile overrides:" -ForegroundColor Cyan
foreach ($m in $Manifests) {
    if (Test-Path -LiteralPath $m) {
        try {
            if (Test-Path -LiteralPath "$m.bak") { Remove-Item -LiteralPath "$m.bak" -Force }
            Rename-Item -LiteralPath $m -NewName ((Split-Path $m -Leaf) + '.bak') -ErrorAction Stop
            if (-not (Test-Backed $backup.manifests $m $null)) { [void]$backup.manifests.Add($m) }
            Write-Host "  [disabled] $m" -ForegroundColor Green
        } catch { Write-Host "  [skipped] $m" -ForegroundColor Yellow }
    }
}

# 3. Shortcuts: desktop, then shell:programs, then shell:common programs, then taskbar pins
foreach ($label in $Locations.Keys) {
    $loc = $Locations[$label]
    Write-Host "`n$label :" -ForegroundColor Cyan
    if (-not $loc -or -not (Test-Path -LiteralPath $loc)) { Write-Host '  (folder not found)' -ForegroundColor DarkGray; continue }
    foreach ($file in Get-ChildItem -LiteralPath $loc -Filter *.lnk -Recurse -ErrorAction SilentlyContinue) {
        $match = $null
        foreach ($pattern in $ShortcutMap.Keys) {
            if ($file.BaseName -like $pattern) { $match = $ShortcutMap[$pattern]; break }
        }
        if (-not $match) { continue }
        $ico = Get-IconPath $match
        if (-not (Test-Path $ico)) { continue }
        try {
            $lnk = $shell.CreateShortcut($file.FullName)
            if (-not (Test-Backed $backup.shortcuts $file.FullName $null)) {
                [void]$backup.shortcuts.Add([pscustomobject]@{ Path = $file.FullName; Old = $lnk.IconLocation })
            }
            $lnk.IconLocation = "$ico,0"
            $lnk.Save()
            (Get-Item -LiteralPath $file.FullName).LastWriteTime = Get-Date
            Write-Host "  [icon] $($file.BaseName) -> $match" -ForegroundColor Green
            if ($label -like 'Start menu*' -and -not $lnk.Arguments -and $lnk.TargetPath -like '*.exe' -and (Test-Path -LiteralPath $lnk.TargetPath)) {
                Set-TileManifest $lnk.TargetPath $match
                (Get-Item -LiteralPath $file.FullName).LastWriteTime = Get-Date
            }
        } catch {
            Write-Host "  [skipped] $($file.BaseName) (probably a Microsoft Store app)" -ForegroundColor Yellow
        }
    }
}

# 4. Desktop folders
Write-Host "`nFolders:" -ForegroundColor Cyan
foreach ($name in $FolderMap.Keys) {
    $folder = Join-Path $Desktop $name
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { continue }
    $ico = Get-IconPath $FolderMap[$name]
    if (-not (Test-Path $ico)) { continue }
    $ini = Join-Path $folder 'desktop.ini'
    $oldIni = $null
    if (Test-Path -LiteralPath $ini) {
        attrib -h -s "$ini" | Out-Null
        $oldIni = Get-Content -LiteralPath $ini -Raw
    }
    if (-not (Test-Backed $backup.folders $folder $null)) {
        [void]$backup.folders.Add([pscustomobject]@{ Path = $folder; OldIni = $oldIni })
    }
    Set-Content -LiteralPath $ini -Value "[.ShellClassInfo]`r`nIconResource=$ico,0`r`n" -Encoding Unicode
    attrib +h +s "$ini" | Out-Null
    attrib +r "$folder" | Out-Null
    Write-Host "  [icon] $name -> $($FolderMap[$name])" -ForegroundColor Green
}

# 5. This PC, Recycle Bin, Control Panel
Write-Host "`nSystem icons:" -ForegroundColor Cyan
foreach ($clsid in $ClsidMap.Keys) {
    $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\$clsid\DefaultIcon"
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    $values = $ClsidMap[$clsid]
    foreach ($vn in $values.Keys) {
        $ico = Get-IconPath $values[$vn]
        if (-not (Test-Path $ico)) { continue }
        $oldVal = (Get-ItemProperty -Path $key -Name $vn -ErrorAction SilentlyContinue).$vn
        if (-not (Test-Backed $backup.registry $key $vn)) {
            [void]$backup.registry.Add([pscustomobject]@{ Path = $key; Name = $vn; Old = $oldVal })
        }
        Set-ItemProperty -Path $key -Name $vn -Value "$ico,0"
    }
    Write-Host "  [icon] $clsid" -ForegroundColor Green
}

# 6. Save backup and refresh
$backup | ConvertTo-Json -Depth 5 | Set-Content -Path $BackupFile -Encoding UTF8
Refresh-Icons

Write-Host "`nDone! Backup saved to $BackupFile" -ForegroundColor Cyan
Write-Host 'Start tiles: unpin and re-pin any tile that still shows the old logo.' -ForegroundColor Cyan
Write-Host 'After app updates, just run apply-icons.bat again.' -ForegroundColor Cyan
Read-Host 'Press Enter to close'

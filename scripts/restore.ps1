# Puts the setup from this repo back onto your PC. Asks before each part.
. (Join-Path $PSScriptRoot 'common.ps1')

function Ask([string]$q) {
    $a = Read-Host "$q [Y/n]"
    return ($a -eq '' -or $a -match '^(y|yes)$')
}

Say "Restoring your Nightfall setup from:`n  $Repo`n" Cyan

if (Ask "Restore and build Moon Clock in $($Live.MoonClock)?") {
    $dest = Find-Folder $Live.MoonClock 'MoonClock.cs'
    if (-not $dest) { $dest = $Live.MoonClock }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Tree (Join-Path $Repo 'moon-clock') $dest | Out-Null
    Say "  Moon Clock copied. Building..." Green
    Start-Process (Join-Path $dest 'build.bat') -WorkingDirectory $dest
}

$needsLoad = $false
$parts = @(
    @{ Key = 'Tab';   Folder = 'nightfall-tab';   Name = 'Nightfall Tab' },
    @{ Key = 'Theme'; Folder = 'nightfall-theme'; Name = 'Nightfall Theme' }
)
foreach ($p in $parts) {
    if (Ask "Restore $($p.Name) to $($Live[$p.Key])?") {
        $dest = Find-Folder $Live[$p.Key] 'manifest.json'
        if (-not $dest) { $dest = $Live[$p.Key]; $needsLoad = $true }
        Copy-Tree (Join-Path $Repo $p.Folder) $dest -Mirror | Out-Null
        Say "  $($p.Name) copied to $dest" Green
    }
}
if ($needsLoad) { Say "`n  In Brave: open brave://extensions, turn on Developer mode, click Load unpacked, and pick the folder(s) above." Yellow }
else { Say "`n  In Brave: open brave://extensions and click the reload arrow on Nightfall Tab and Nightfall Theme." }

if (Ask "`nRemove shortcut arrows from desktop icons?") { Start-Process (Join-Path $Repo 'tweaks\remove-arrows.bat') }

Say "`nAll done." Green
Read-Host "Press Enter to close"

# Copies your current setup into this repo (tools and visuals only), then commits and pushes if Git is set up.
. (Join-Path $PSScriptRoot 'common.ps1')

Say "Backing up your Nightfall setup into:`n  $Repo`n" Cyan
$private = @()
foreach ($n in $PrivateIcons) { $private += "$n.ico"; $private += "$n-150.png"; $private += "$n-70.png" }

function Report([string]$name, [bool]$ok) {
    if ($ok) { Say "  ${name}: updated" Green } else { Say "  ${name}: problem copying" Yellow }
}

# Icons (your original set only)
if (Test-Path -LiteralPath $Live.Icons) {
    $ok = Copy-Tree $Live.Icons (Join-Path $Repo 'icons\icons') @('*.ico') $private
    $tiles = Join-Path $Live.Icons 'tiles'
    if (Test-Path -LiteralPath $tiles) { $ok = (Copy-Tree $tiles (Join-Path $Repo 'icons\tiles') @('*.png') $private) -and $ok }
    Report 'Icons' $ok
} else { Say "  Icons: not found at $($Live.Icons), skipped" Yellow }

# Moon Clock source (never the .exe or your personal settings)
$mc = Find-Folder $Live.MoonClock 'MoonClock.cs'
if ($mc) {
    Report 'Moon Clock' (Copy-Tree $mc (Join-Path $Repo 'moon-clock') @('*.cs', 'build.bat', 'moon.ico', 'moon.png', 'thoughts.txt', 'CREDITS.txt'))
} else { Say "  Moon Clock: not found under $($Live.MoonClock), skipped" Yellow }

# Browser extensions
$parts = @(
    @{ Key = 'Tab';   Folder = 'nightfall-tab';   Name = 'Nightfall Tab' },
    @{ Key = 'Theme'; Folder = 'nightfall-theme'; Name = 'Nightfall Theme' }
)
foreach ($p in $parts) {
    $src = Find-Folder $Live[$p.Key] 'manifest.json'
    if ($src) { Report $p.Name (Copy-Tree $src (Join-Path $Repo $p.Folder) -Mirror) }
    else { Say "  $($p.Name): not found under $($Live[$p.Key]), skipped" Yellow }
}

# Git: commit and push if this folder is a Git repository
$git = Get-Command git -ErrorAction SilentlyContinue
if ($git -and (Test-Path (Join-Path $Repo '.git'))) {
    Say "`nChanges:" Cyan
    $changes = git -C $Repo status --short
    if (-not $changes) { Say "  Nothing changed since your last backup." Green }
    else {
        $changes | ForEach-Object { Say "  $_" }
        git -C $Repo add -A | Out-Null
        git -C $Repo commit -m ("Backup " + (Get-Date -Format 'yyyy-MM-dd HH:mm')) | Out-Null
        Say "`nUploading to GitHub..." Cyan
        git -C $Repo push
        if ($LASTEXITCODE -eq 0) { Say "Done! Your backup is on GitHub." Green }
        else { Say "Saved locally, but the upload failed. Check your internet or GitHub sign-in, then run backup again." Yellow }
    }
} else {
    Say "`nFiles are updated in this folder." Green
    Say "To upload: open GitHub Desktop, click Commit, then Push. (Or connect this folder with Git once; see the README.)"
}
Read-Host "`nPress Enter to close"

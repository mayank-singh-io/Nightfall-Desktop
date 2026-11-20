# Shared settings for backup.ps1 and restore.ps1. Edit these if your folders live somewhere else.
$Repo = Split-Path $PSScriptRoot -Parent

$Live = @{
    Icons     = 'C:\Icons'
    MoonClock = 'C:\Moon Clock'
    Tab       = 'C:\Nightfall Tab'
    Theme     = 'C:\Nightfall Theme'
}

# Icons made by other designers (versions of company logos). They stay on your PC and never go public.
$PrivateIcons = @('firefox', 'brave', 'vscode', 'termius', 'ollama')

# Finds the real folder even if a zip was extracted into a subfolder, e.g. C:\Nightfall Tab\nightfall-tab
function Find-Folder([string]$root, [string]$marker) {
    if (-not (Test-Path -LiteralPath $root)) { return $null }
    if (Test-Path -LiteralPath (Join-Path $root $marker)) { return $root }
    $hit = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName $marker) } | Select-Object -First 1
    if ($hit) { return $hit.FullName }
    return $null
}

# robocopy wrapper: returns $true on success (robocopy uses codes 0-7 for success)
function Copy-Tree([string]$from, [string]$to, [string[]]$files = @('*.*'), [string[]]$excludeFiles = @(), [switch]$Mirror) {
    $rc = @($from, $to) + $files + @('/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    if ($Mirror) { $rc += '/MIR' } else { $rc += '/E' }
    if ($excludeFiles.Count -gt 0) { $rc += '/XF'; $rc += $excludeFiles }
    & robocopy @rc | Out-Null
    return ($LASTEXITCODE -lt 8)
}

function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }

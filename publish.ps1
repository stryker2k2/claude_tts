<#
.SYNOPSIS
    Builds claude_tts, installs it to ~/.claude/tts/, creates a Start Menu shortcut,
    and optionally adds a Startup shortcut for auto-launch at login.

.PARAMETER InstallDir
    Destination folder for the published exe and config.json.
    Default: C:\Users\<you>\.claude\tts

.PARAMETER AutoStart
    When set, adds a Startup shortcut so the server launches at Windows login.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -AutoStart
    .\publish.ps1 -InstallDir "C:\Tools\claude_tts"
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:USERPROFILE ".claude\tts"),
    [switch]$AutoStart
)

$ErrorActionPreference = "Stop"
$projectDir = $PSScriptRoot

# 1. Build
Write-Host ""
Write-Host "Step 1/4 - Building..." -ForegroundColor Cyan
& "$projectDir\build.ps1" -Configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 2. Install
Write-Host ""
Write-Host "Step 2/4 - Installing to $InstallDir..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$publishDir = Join-Path $projectDir "bin\publish"
Copy-Item "$publishDir\*" -Destination $InstallDir -Recurse -Force
Write-Host "Files copied." -ForegroundColor Green

# 3. Start Menu shortcut (always created/updated)
Write-Host ""
Write-Host "Step 3/4 - Creating Start Menu shortcut..." -ForegroundColor Cyan
$exePath      = Join-Path $InstallDir "claude_tts.exe"
$startMenuDir = Join-Path ([Environment]::GetFolderPath("Programs")) "Claude TTS"
New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null
$startMenuPath = Join-Path $startMenuDir "Claude TTS Server.lnk"

$shell    = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($startMenuPath)
$shortcut.TargetPath       = $exePath
$shortcut.WorkingDirectory = $InstallDir
$shortcut.WindowStyle      = 1
$shortcut.Description      = "Claude TTS named-pipe server"
$shortcut.Save()

Write-Host "Start Menu shortcut updated: $startMenuPath" -ForegroundColor Green

# 4. Optional: Windows Startup shortcut
if ($AutoStart) {
    Write-Host ""
    Write-Host "Step 4/4 - Creating Startup shortcut..." -ForegroundColor Cyan
    $startupFolder = [Environment]::GetFolderPath("Startup")
    $startupPath   = Join-Path $startupFolder "Claude TTS Server.lnk"

    $shortcut = $shell.CreateShortcut($startupPath)
    $shortcut.TargetPath       = $exePath
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.WindowStyle      = 7
    $shortcut.Description      = "Claude TTS named-pipe server"
    $shortcut.Save()

    Write-Host "Startup shortcut created: $startupPath" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Step 4/4 - Skipped auto-start (re-run with -AutoStart to enable)." -ForegroundColor DarkGray
}

# Done
$exeFinal = Join-Path $InstallDir "claude_tts.exe"
Write-Host ""
Write-Host "Done! Installed to: $InstallDir" -ForegroundColor Green
Write-Host ""
Write-Host "Start the server:"
Write-Host "  $exeFinal"
Write-Host ""
Write-Host "Or minimized:"
Write-Host "  Start-Process '$exeFinal' -WindowStyle Minimized"

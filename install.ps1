<#
.SYNOPSIS
    Builds claude_tts, installs it to ~/.claude/tts/, and updates tts-hook.ps1
    to send text to the server (with System.Speech fallback when server is off).

.PARAMETER InstallDir
    Destination folder for the published exe and config.json.
    Default: C:\Users\<you>\.claude\tts

.PARAMETER AutoStart
    When set, adds a Startup shortcut so the server launches at Windows login.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -AutoStart
    .\install.ps1 -InstallDir "C:\Tools\claude_tts"
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
Write-Host "Step 1/3 — Building..." -ForegroundColor Cyan
& "$projectDir\build.ps1" -Configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 2. Install
Write-Host ""
Write-Host "Step 2/3 — Installing to $InstallDir..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$publishDir = Join-Path $projectDir "bin\publish"
Copy-Item "$publishDir\*" -Destination $InstallDir -Recurse -Force
Write-Host "Files copied." -ForegroundColor Green

# 3. Optional: Windows Startup shortcut
if ($AutoStart) {
    Write-Host ""
    Write-Host "Step 3/3 — Creating Startup shortcut..." -ForegroundColor Cyan
    $startupFolder = [Environment]::GetFolderPath("Startup")
    $shortcutPath  = Join-Path $startupFolder "Claude TTS Server.lnk"
    $exePath       = Join-Path $InstallDir "claude_tts.exe"

    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath      = $exePath
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.WindowStyle     = 7  # minimized
    $shortcut.Description     = "Claude TTS named-pipe server"
    $shortcut.Save()

    Write-Host "Startup shortcut created: $shortcutPath" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Step 3/3 — Skipped auto-start (re-run with -AutoStart to enable)." -ForegroundColor DarkGray
}

# Done
$exeFinal = Join-Path $InstallDir "claude_tts.exe"
Write-Host ""
Write-Host "Installation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Start the server now:"
Write-Host "  $exeFinal"
Write-Host ""
Write-Host "Or start minimized in a new window:"
Write-Host "  Start-Process '$exeFinal' -WindowStyle Minimized"
Write-Host ""
Write-Host "The tts-hook.ps1 in ~/.claude/ already uses the pipe with System.Speech fallback."
Write-Host "When the server is running, responses are spoken via Natural HD voices."
Write-Host "When stopped, it falls back to System.Speech (David Desktop)."

#Requires -Version 5.1
<#
.SYNOPSIS
    One-shot setup for new developers: downloads Piper, builds the project,
    and creates config.json from the example if one does not already exist.
.PARAMETER Voice
    Voice model to download (default: en_US-ryan-high).
    Full list: https://rhasspy.github.io/piper-samples/
.PARAMETER SelfContained
    Pass -SelfContained to build a portable exe with the .NET runtime bundled.
#>
param(
    [string]$Voice = "en_US-ryan-high",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# ---------------------------------------------------------------------------
# 1. Download Piper TTS binary + voice model
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Step 1: Piper TTS ===" -ForegroundColor Yellow
& "$PSScriptRoot\download-piper.ps1" -Voice $Voice

# ---------------------------------------------------------------------------
# 2. Build
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Step 2: Build ===" -ForegroundColor Yellow
$buildArgs = if ($SelfContained) { @("-SelfContained") } else { @() }
& "$PSScriptRoot\build.ps1" @buildArgs

# ---------------------------------------------------------------------------
# 3. config.json — copy example if not present
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Step 3: Config ===" -ForegroundColor Yellow

$publishConfig = Join-Path $PSScriptRoot "bin\publish\config.json"
if (Test-Path $publishConfig) {
    Write-Host "    config.json already exists in bin\publish, skipping." -ForegroundColor Green
} else {
    $exampleConfig = Join-Path $PSScriptRoot "config.example.json"
    Copy-Item $exampleConfig $publishConfig
    Write-Host "    Created bin\publish\config.json from config.example.json" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=============================" -ForegroundColor Yellow
Write-Host " Setup complete!" -ForegroundColor Green
Write-Host "=============================" -ForegroundColor Yellow
Write-Host ""
Write-Host "To start the TTS server:" -ForegroundColor Cyan
Write-Host "    .\bin\publish\claude_tts.exe"
Write-Host ""
Write-Host "To install and add to Windows startup:"
Write-Host "    .\publish.ps1 -AutoStart"
Write-Host ""

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

$piperDir = Join-Path $PSScriptRoot "piper"
New-Item -ItemType Directory -Force -Path $piperDir | Out-Null

# Piper binary
$piperExePath = Join-Path $piperDir "piper.exe"
if (Test-Path $piperExePath) {
    Write-Host "==> Piper binary already installed, skipping." -ForegroundColor Green
} else {
    Write-Host "==> Fetching latest Piper release from GitHub..." -ForegroundColor Cyan

    $releaseApi = "https://api.github.com/repos/rhasspy/piper/releases/latest"
    $headers    = @{ "User-Agent" = "claude_tts-downloader" }

    $release = Invoke-RestMethod -Uri $releaseApi -Headers $headers
    Write-Host "    Version: $($release.tag_name)"

    $winAsset = $release.assets |
        Where-Object { $_.name -match "windows.*(amd64|x86_64)" } |
        Select-Object -First 1

    if (-not $winAsset) {
        $winAsset = $release.assets |
            Where-Object { $_.name -like "*windows*" -and $_.name -like "*.zip" } |
            Select-Object -First 1
    }

    if (-not $winAsset) {
        Write-Error "Could not locate a Windows release asset in $($release.tag_name). Check https://github.com/rhasspy/piper/releases manually."
        exit 1
    }

    Write-Host "    Asset  : $($winAsset.name)"

    $tmpZip = Join-Path $env:TEMP "piper_windows_dl.zip"
    Write-Host "    Downloading binary..."
    Invoke-WebRequest -Uri $winAsset.browser_download_url -OutFile $tmpZip -UseBasicParsing

    Write-Host "    Extracting..."
    $tmpExtract = Join-Path $env:TEMP "piper_extract_$(Get-Random)"
    Expand-Archive -Path $tmpZip -DestinationPath $tmpExtract -Force

    $piperExeInZip = Get-ChildItem -Path $tmpExtract -Recurse -Filter "piper.exe" | Select-Object -First 1
    if (-not $piperExeInZip) {
        Write-Error "piper.exe not found inside the downloaded zip."
        exit 1
    }

    Copy-Item -Path "$($piperExeInZip.DirectoryName)\*" -Destination $piperDir -Recurse -Force
    Remove-Item $tmpExtract -Recurse -Force
    Remove-Item $tmpZip     -Force

    Write-Host "    Installed to: $piperDir" -ForegroundColor Green
}

# Voice model
Write-Host ""
Write-Host "==> Downloading voice model: $Voice" -ForegroundColor Cyan

if ($Voice -notmatch '^([a-z]{2})_([A-Z]{2})-(.+)-(.+)$') {
    Write-Error "Voice name '$Voice' does not match expected pattern 'lang_REGION-name-quality' (e.g. en_US-ryan-high)."
    exit 1
}

$lang    = $Matches[1]
$region  = $Matches[2]
$name    = $Matches[3]
$quality = $Matches[4]

$hfBase = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0"
$hfPath = "$hfBase/$lang/${lang}_${region}/$name/$quality"

$onnxFile     = "$Voice.onnx"
$onnxJsonFile = "$Voice.onnx.json"

foreach ($file in @($onnxFile, $onnxJsonFile)) {
    $dest = Join-Path $piperDir $file
    if (Test-Path $dest) {
        Write-Host "    Skipping $file (already exists)"
        continue
    }
    Write-Host "    Downloading $file..."
    Invoke-WebRequest -Uri "$hfPath/$file" -OutFile $dest -UseBasicParsing
}

Write-Host "    Voice model installed." -ForegroundColor Green

# Auto-update config.json files
foreach ($configPath in @(
    (Join-Path $PSScriptRoot "config.json"),
    (Join-Path $PSScriptRoot "bin\publish\config.json")
)) {
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        $cfg | Add-Member -MemberType NoteProperty -Name "piperExe"   -Value "..\..\piper\piper.exe" -Force
        $cfg | Add-Member -MemberType NoteProperty -Name "piperModel" -Value "..\..\piper\$onnxFile" -Force
        $cfg | ConvertTo-Json -Depth 5 | Set-Content $configPath -Encoding UTF8
        Write-Host "    Updated: $configPath" -ForegroundColor Green
    }
}

# ---------------------------------------------------------------------------
# 2. Build
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Step 2: Build ===" -ForegroundColor Yellow

$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" }
          elseif (Test-Path "C:\Program Files\dotnet\dotnet.exe") { "C:\Program Files\dotnet\dotnet.exe" }
          else { Write-Host "ERROR: dotnet SDK not found. Install from https://dotnet.microsoft.com/download" -ForegroundColor Red; exit 1 }

& $dotnet publish "$PSScriptRoot\claude_tts.csproj" `
    --configuration Release `
    --runtime win-x64 `
    --self-contained $(if ($SelfContained) { "true" } else { "false" }) `
    --output (Join-Path $PSScriptRoot "bin\publish")

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

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
Write-Host "To install and launch from Start Menu:" -ForegroundColor Cyan
Write-Host "    .\install.ps1"
Write-Host ""
Write-Host "To run directly (development):" -ForegroundColor Cyan
Write-Host "    dotnet run"
Write-Host ""

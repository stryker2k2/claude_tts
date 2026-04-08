#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads the Piper TTS binary and the en_US-ryan-high voice model.
.DESCRIPTION
    Fetches the latest Windows x64 Piper release from GitHub, extracts it,
    then downloads the en_US-ryan-high.onnx voice model from Hugging Face.
    Everything lands in piper\ at the repo root.
.PARAMETER Voice
    The voice model to download (default: en_US-ryan-high).
    Other options: en_US-amy-low, en_US-joe-medium, en_GB-cori-high, etc.
    Full list: https://rhasspy.github.io/piper-samples/
#>
param(
    [string]$Voice = "en_US-ryan-high"
)

$ErrorActionPreference = "Stop"

$piperDir = Join-Path $PSScriptRoot "piper"

New-Item -ItemType Directory -Force -Path $piperDir | Out-Null

# ---------------------------------------------------------------------------
# 1. Piper binary (GitHub latest release) — skipped if already installed
# ---------------------------------------------------------------------------
Write-Host ""

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
    # Fallback: look for any zip asset
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

# The zip may contain a 'piper/' sub-folder or dump files at the root.
$piperExeInZip = Get-ChildItem -Path $tmpExtract -Recurse -Filter "piper.exe" |
                 Select-Object -First 1
if (-not $piperExeInZip) {
    Write-Error "piper.exe not found inside the downloaded zip."
    exit 1
}

# Copy all sibling files (piper.exe + DLLs) to piperDir
$sourceFolder = $piperExeInZip.DirectoryName
Copy-Item -Path "$sourceFolder\*" -Destination $piperDir -Recurse -Force
Remove-Item $tmpExtract -Recurse -Force
Remove-Item $tmpZip     -Force

Write-Host "    Installed to: $piperDir" -ForegroundColor Green
} # end if piper binary not present

# ---------------------------------------------------------------------------
# 2. Voice model (Hugging Face — rhasspy/piper-voices)
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "==> Downloading voice model: $Voice" -ForegroundColor Cyan

# Parse voice name into parts: lang_REGION-name-quality
# e.g. en_US-ryan-high -> lang=en, region=US, name=ryan, quality=high
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

# ---------------------------------------------------------------------------
# 3. Print summary
# ---------------------------------------------------------------------------
$piperExe   = Join-Path $piperDir "piper.exe"
$piperModel = Join-Path $piperDir $onnxFile

Write-Host ""
Write-Host "Done! Piper installed to: $piperDir" -ForegroundColor Yellow
Write-Host ""
Write-Host "config.json paths (relative to exe in bin\publish):"
Write-Host "  `"piperExe`"   : `"..\\..\\piper\\piper.exe`","
Write-Host "  `"piperModel`" : `"..\\..\\piper\\$onnxFile`""
Write-Host ""

# Auto-update both config.json files
foreach ($configPath in @(
    (Join-Path $PSScriptRoot "config.json"),
    (Join-Path $PSScriptRoot "bin\publish\config.json")
)) {
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        $cfg | Add-Member -MemberType NoteProperty -Name "piperExe"   -Value "..\..\piper\piper.exe"   -Force
        $cfg | Add-Member -MemberType NoteProperty -Name "piperModel" -Value "..\..\piper\$onnxFile"   -Force
        $cfg | ConvertTo-Json -Depth 5 | Set-Content $configPath -Encoding UTF8
        Write-Host "Updated: $configPath" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Restart the server and Piper will be used automatically." -ForegroundColor Cyan

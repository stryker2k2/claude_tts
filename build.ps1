<#
.SYNOPSIS
    Builds and publishes claude_tts.

.PARAMETER Configuration
    Build configuration: Debug or Release (default: Release).

.PARAMETER SelfContained
    When set, bundles the .NET runtime so the exe runs on machines without
    .NET 10 installed. Produces a larger binary (~60 MB vs ~1 MB).

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File .\build.ps1
    powershell.exe -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Debug
    powershell.exe -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$projectDir = $PSScriptRoot

# Resolve dotnet — falls back to known install path if not on PATH
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if ($dotnet) {
    $dotnet = "dotnet"
} elseif (Test-Path "C:\Program Files\dotnet\dotnet.exe") {
    $dotnet = "C:\Program Files\dotnet\dotnet.exe"
} else {
    Write-Host "ERROR: dotnet SDK not found. Install from https://dotnet.microsoft.com/download" -ForegroundColor Red
    exit 1
}
$outputDir  = Join-Path $projectDir "bin\publish$(if ($SelfContained) { '-standalone' })"

Write-Host ""
Write-Host "Claude TTS - Build" -ForegroundColor Cyan
Write-Host "Configuration : $Configuration"
Write-Host "Self-contained: $($SelfContained.IsPresent)"
Write-Host "Output        : $outputDir"
Write-Host ""

& $dotnet publish "$projectDir\claude_tts.csproj" `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained $(if ($SelfContained.IsPresent) { "true" } else { "false" }) `
    --output $outputDir

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Build FAILED (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Build succeeded!" -ForegroundColor Green
Write-Host ""
Write-Host "Executable : $outputDir\claude_tts.exe"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  Test voices : $outputDir\claude_tts.exe --list"
Write-Host '  Test speech : claude_tts.exe --speak "Hello world"'
Write-Host "  Install     : .\install.ps1"

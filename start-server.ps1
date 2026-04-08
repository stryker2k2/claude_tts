#Requires -Version 5.1
<#
.SYNOPSIS
    Starts the Claude TTS server.
#>

$exe = Join-Path $PSScriptRoot "bin\publish\claude_tts.exe"

if (-not (Test-Path $exe)) {
    Write-Error "claude_tts.exe not found. Run build.ps1 first."
    exit 1
}

Start-Process $exe -WindowStyle Normal

Start-Sleep -Milliseconds 1000
$proc = Get-Process claude_tts -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "Server started (PID $($proc.Id))." -ForegroundColor Green
} else {
    Write-Host "Process not detected yet -- it may still be starting." -ForegroundColor Yellow
}

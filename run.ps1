# -----------------------------------------------------------------------------
# Starts the Fatura API (new window) and the Fatura web UI (this window).
#
#   powershell -ExecutionPolicy Bypass -File .\run.ps1
#
# Uses $env:GEMINI_API_KEY if set, otherwise asks for it once. The key stays on
# the server side: website visitors never see or need it.
# -----------------------------------------------------------------------------

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $env:GEMINI_API_KEY) {
    $env:GEMINI_API_KEY = Read-Host "Gemini API key"
}

Write-Host "Starting Fatura API on http://localhost:5080 ..." -ForegroundColor Cyan
# The new window inherits GEMINI_API_KEY from this session's environment.
Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$PSScriptRoot'; dotnet run --project src/Fatura.Api"

# Wait until the API answers /health (first build can take ~30 s).
$ready = $false
for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
    Start-Sleep -Seconds 2
    try { $ready = (Invoke-RestMethod http://localhost:5080/health -TimeoutSec 2).status -eq "ok" } catch { }
}
if (-not $ready) { Write-Host "API did not start - check the other window for errors." -ForegroundColor Red; exit 1 }

Write-Host "Starting Fatura UI on http://localhost:8501 ..." -ForegroundColor Cyan
Set-Location "$PSScriptRoot\ui"
if (Get-Command py -ErrorAction SilentlyContinue) { py -m streamlit run app.py } else { python -m streamlit run app.py }

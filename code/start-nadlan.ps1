# Starts the Nadlan web app on http://localhost:5515.
# Brings the DB structure up to date first (via update-db.ps1); the app itself never changes the schema.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSCommandPath
. (Join-Path $root "scripts\Set-NadlanDbEnv.ps1")

& (Join-Path $root "update-db.ps1")
if ($LASTEXITCODE -ne 0) { throw "Database update failed; not starting the app." }

Write-Host ""
Write-Host "Starting Nadlan on http://localhost:5515 ..." -ForegroundColor Cyan
& dotnet run --project (Join-Path $root "src\Nadlan.Host\Nadlan.Host.csproj")

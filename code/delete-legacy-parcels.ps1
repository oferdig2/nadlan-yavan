# Deletes the legacy (Airtable) Parcels - the ones with an invented TMP- KAEK - from your local database.
# First shows what it would delete and asks you to type "yes". Kept (and listed): legacy Parcels that carry an Asset
# or files. Each deletion is in the Parcel history with its KAEK and polygon.
# On the server the same thing is:  sudo nadlan-db parcels delete-provisional [--yes]
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSCommandPath
. (Join-Path $root "scripts\Set-NadlanDbEnv.ps1")
$tool = Join-Path $root "src\Nadlan.DbTool\Nadlan.DbTool.csproj"

& dotnet run --project $tool -- parcels delete-provisional
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
$typed = Read-Host "Delete them? Type yes to delete"
if ($typed -ne "yes") { Write-Host "Not confirmed; nothing deleted."; exit 1 }

& dotnet run --project $tool -- parcels delete-provisional --yes
exit $LASTEXITCODE

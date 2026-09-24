# Loads the legacy Airtable polygons as demo Parcels. Missing KAEKs get provisional TMP- ids (flagged in the DB and UI).
# Safe to re-run: existing Parcels are found by their registry id and not duplicated.
param(
    [string]$Csv = (Join-Path (Split-Path -Parent $PSCommandPath) "..\AI\Gelem\old system data\inventory tables\Inventory-Grid view.csv"),
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSCommandPath
if (-not $DryRun) {
    . (Join-Path $root "scripts\Set-NadlanDbEnv.ps1")
    & (Join-Path $root "update-db.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Database update failed." }
}

$importArgs = @("run", "--project", (Join-Path $root "src\Nadlan.Import\Nadlan.Import.csproj"), "--", "parcels", $Csv)
if ($DryRun) { $importArgs += "--dry-run" }
& dotnet @importArgs

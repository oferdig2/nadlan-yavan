# Creates the nadlanyavan schema if needed and applies pending table changes (src/Nadlan.Persistence.MySql/Sql/NNN_*.sql).
# Run after pulling code that adds a new Sql script. Safe to run any time.
#   .\update-db.ps1           apply pending changes
#   .\update-db.ps1 -Status   show the schema version only
#   .\update-db.ps1 -Reset    DEV ONLY: drop the schema with ALL its data and rebuild it (asks you to type its name)
param([switch]$Status, [switch]$Reset)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSCommandPath
. (Join-Path $root "scripts\Set-NadlanDbEnv.ps1")
$tool = Join-Path $root "src\Nadlan.DbTool\Nadlan.DbTool.csproj"

if ($Reset) {
    $db = ([regex]::Match($env:NADLAN_MYSQL_CS, '(?i)database=([^;]+)')).Groups[1].Value
    Write-Host "This DELETES the schema '$db' and all its data, then rebuilds empty tables." -ForegroundColor Red
    $typed = Read-Host "Type the schema name to confirm"
    if ($typed -ne $db) { Write-Host "Not confirmed; nothing changed."; exit 1 }
    & dotnet run --project $tool -- reset $typed
    exit $LASTEXITCODE
}

$command = if ($Status) { "status" } else { "migrate" }
& dotnet run --project $tool -- $command
exit $LASTEXITCODE

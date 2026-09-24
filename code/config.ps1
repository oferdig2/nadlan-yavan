# Reads/writes app_config (same idea as Futuristic's DB-first config).
#   .\config.ps1 list
#   .\config.ps1 show ms:host
#   .\config.ps1 set ms:host Nadlan:Maps:GoogleApiKey AIza...
# Restart the app after a change.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSCommandPath
. (Join-Path $root "scripts\Set-NadlanDbEnv.ps1")

$toolArgs = @("run", "--project", (Join-Path $root "src\Nadlan.DbTool\Nadlan.DbTool.csproj"), "--", "config") + $args
& dotnet @toolArgs
exit $LASTEXITCODE

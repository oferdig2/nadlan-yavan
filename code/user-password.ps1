# Sets a user's password straight in the database - for the first sign-in before Google is configured, or to get back
# in when locked out. Asks for the MySQL password (if not set yet) and the new password; nothing goes on the command line.
#   .\user-password.ps1 -Email oferdig2@gmail.com
#   .\user-password.ps1 -List                        (show users)
# Everyday password handling is in the app: Admin > Users (set password, reset link, remove password).
param([string]$Email, [switch]$List)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSCommandPath
. (Join-Path $root "scripts\Set-NadlanDbEnv.ps1")
$tool = Join-Path $root "src\Nadlan.DbTool\Nadlan.DbTool.csproj"

if ($List -or -not $Email) {
    & dotnet run --project $tool -- user list
    if (-not $Email) { Write-Host "Usage: .\user-password.ps1 -Email <email>"; }
    exit $LASTEXITCODE
}

function Read-Secret([string]$prompt) {
    $secure = Read-Host -Prompt $prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$first = Read-Secret "New password for $Email (at least 10 characters)"
$second = Read-Secret "Repeat it"
if ($first -ne $second) { Write-Host "The two entries differ; nothing changed." -ForegroundColor Red; exit 1 }

$env:NADLAN_NEW_PASSWORD = $first
try {
    & dotnet run --project $tool -- user set-password $Email
    $code = $LASTEXITCODE
} finally {
    Remove-Item Env:\NADLAN_NEW_PASSWORD -ErrorAction SilentlyContinue
}
exit $code

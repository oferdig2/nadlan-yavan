# Builds NADLAN_MYSQL_CS for this PowerShell window, asking for the MySQL password if it is not set yet.
# Same pattern as Futuristic SaaS start-saas-dbfirst.ps1 (FUTURISTIC_MYSQL_CS): the DB password is the only
# bootstrap secret; everything else is loaded from the app_config table.
param(
    [string]$Server = "localhost",
    [string]$User = "root",
    [string]$Database = "nadlanyavan"
)

if ($env:NADLAN_MYSQL_CS) {
    return
}

Write-Host ""
Write-Host "NADLAN_MYSQL_CS is not set." -ForegroundColor Yellow
Write-Host "Enter the MySQL $User password for local dev (connection: $Server, db=$Database)." -ForegroundColor Yellow
$secure = Read-Host -Prompt "MySQL $User password" -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}
if ([string]::IsNullOrWhiteSpace($password)) {
    throw "MySQL password is required."
}

# Quoted so a ';' or '=' in the password cannot break the connection string.
$quoted = '"' + $password.Replace('"', '""') + '"'
$env:NADLAN_MYSQL_CS = "Server=$Server;Userid=$User;password=$quoted;database=$Database;Max Pool Size=100;Connection Timeout=20;default command timeout=300;"

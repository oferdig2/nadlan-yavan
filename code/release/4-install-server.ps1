<#
.SYNOPSIS
Installs Nadlan on an Amazon Linux 2023 server (EC2) over SSH: MySQL, nginx, HTTPS, the app. Safe to re-run.

.DESCRIPTION
Builds the server package on this PC (self-contained linux-x64 binaries: the server gets no source code, no SDK and
compiles nothing), uploads it, and runs release\server\install.sh on the server:
  - system updates, 1 GB swap (small instances), service user "nadlan"
  - MySQL 8.4 bound to 127.0.0.1 and sized to ~250-300 MB of RAM, a "nadlan" DB user with a generated password
    (or an external MySQL such as RDS: then give its connection string and nothing is installed)
  - /etc/nadlan/nadlan.env with NADLAN_MYSQL_CS - the only thing the app reads from the environment
  - nginx in front of the app; Let's Encrypt HTTPS when a domain is given (its DNS must already point at the server)
  - the app as systemd service "nadlan", daily DB backups at 02:30 UTC (/var/backups/nadlan)
  - settings in app_config for this server: public URL, forwarded headers, Google Maps key, S3 storage
Then optionally sets the Admin's password and the S3 bucket's CORS rule for the site's address.
The database starts empty (no legacy data). To bring your local database: 5-copy-local-db.ps1.
Later code changes: 6-update-server.ps1.
#>
param(
    [string]$SshHost = "",
    [string]$SshUser = "",
    [string]$SshKeyPath = "",
    [string]$Domain = "",
    [switch]$SkipTests
)
. (Join-Path $PSScriptRoot "lib\common.ps1")

$target = Get-SshTarget $SshHost $SshUser $SshKeyPath

Write-Step "Site address"
$Domain = Read-Value "Domain name for the site (its DNS A record -> $($target.Host))" $Domain -Setting "domain" -Optional `
    -Pattern '^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$' -PatternHint "Like nadlan.example.com (lowercase, no http://)."
$certEmail = ""
if ($Domain) {
    $resolved = @(try { [Net.Dns]::GetHostAddresses($Domain) | ForEach-Object { $_.IPAddressToString } } catch { })
    if ($resolved -notcontains $target.Host) {
        Write-Warning "$Domain resolves to '$($resolved -join ', ')', not $($target.Host). HTTPS will be skipped until DNS points here (re-run this script then)."
    }
    $certEmail = Read-Value "Email for Let's Encrypt certificate notices" -Setting "certEmail" -Optional -Pattern '^[^\s@]+@[^\s@]+$'
}
else {
    Write-Warning "Without a domain the site is plain HTTP on $($target.Host): fine for a first look, not for real use."
}

Write-Step "Database"
$connectionString = ""
if (-not (Read-YesNo "Install MySQL on this server? (n = use an external MySQL 8, e.g. RDS)" $true)) {
    while ($true) {
        $connectionString = Read-Secret "Connection string (Server=...;Port=3306;User ID=...;Password=...;Database=nadlanyavan)"
        if ($connectionString -match '(?i)(^|;)\s*database\s*=' -and $connectionString -notmatch "'") { break }
        Write-Host "It must name the database (Database=...) and may not contain a single quote (')." -ForegroundColor Yellow
    }
}

Write-Step "App settings (all can be changed later with 7-server-admin.ps1)"
$mapsKey = Read-Value "Google Maps JavaScript API key (restrict it to your domain in Google Cloud)" -Setting "mapsKey" -Optional
$storageBucket = Read-Value "S3 bucket for files" -Setting "storageBucket" -Optional -Pattern '^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$'
$storageRoot = ""
$storageRegion = ""
if ($storageBucket) {
    $storageRoot = (Read-Value "Folder in the bucket for this server" -Setting "storageRoot" -Default "nadlan/prod" -Pattern '^[A-Za-z0-9._/-]*$').Trim('/')
    $storageRegion = Read-Value "Bucket region" -Setting "awsRegion" -Default "eu-central-1" -Pattern '^[a-z]{2}(-[a-z]+)+-\d$'
    Write-Host "The server reaches S3 with its EC2 instance role (3-create-ec2-server.ps1 creates it); no access keys are stored." -ForegroundColor DarkGray
}

$adminEmail = ""
$adminPassword = ""
if (Read-YesNo "Set an Admin's sign-in password at the end?" $true) {
    $adminEmail = Read-Value "Admin email (the first Admin is created by the database setup)" -Setting "adminEmail" -Default "oferdig2@gmail.com" `
        -Pattern '^[^\s''"`;|&<>()$\\]+@[^\s''"`;|&<>()$\\]+$' -PatternHint "An email address."
    $adminPassword = Read-Secret "Password for $adminEmail (at least 8 characters)" -MinLength 8 -Confirm
}

Write-Step "Server package"
$package = Get-ServerPackage $false (-not $SkipTests)

$conf = @(
    "DOMAIN=$(ConvertTo-BashLiteral $Domain)",
    "CERT_EMAIL=$(ConvertTo-BashLiteral $certEmail)",
    "CONNECTION_STRING=$(ConvertTo-BashLiteral $connectionString)",
    "MAPS_KEY=$(ConvertTo-BashLiteral $mapsKey)",
    "STORAGE_BUCKET=$(ConvertTo-BashLiteral $storageBucket)",
    "STORAGE_ROOT=$(ConvertTo-BashLiteral $storageRoot)",
    "STORAGE_REGION=$(ConvertTo-BashLiteral $storageRegion)"
) -join "`n"
$confPath = Join-Path $env:TEMP ("nadlan-install-" + [guid]::NewGuid().ToString("N") + ".conf")

try {
    $remoteDir = Send-ServerPackage $target $package
    [IO.File]::WriteAllText($confPath, "$conf`n", (New-Object System.Text.UTF8Encoding($false)))
    Copy-ToRemote $target $confPath "nadlan-upload/install.conf"
    Remove-Item $confPath -ErrorAction SilentlyContinue

    Write-Step "Installing on $($target.Host) (first run: ~5-10 minutes)"
    Invoke-RemoteDetached $target "sudo bash $remoteDir/server/install.sh $remoteDir" # survives a dropped SSH connection
}
finally {
    Remove-Item $confPath -ErrorAction SilentlyContinue
    try { Invoke-Remote $target "rm -rf ~/nadlan-upload" } catch { Write-Warning "Couldn't remove ~/nadlan-upload on the server: $($_.Exception.Message)" }
}

if ($adminEmail) {
    Write-Step "Admin password for $adminEmail"
    Invoke-RemoteWithInput $target "sudo /opt/nadlan/current/server/set-password.sh $adminEmail" $adminPassword
}

$url = "http://$($target.Host)"
if ($Domain) {
    $check = Invoke-Native "ssh" (@(Get-SshOptions $target) + @($target.Address, "sudo test -d /etc/letsencrypt/live/$Domain && echo yes"))
    $url = if ($check.Out.Trim() -eq "yes") { "https://$Domain" } else { "http://$Domain" }
}
Set-Setting "publicUrl" $url

if ($storageBucket -and (Read-YesNo "Allow uploads from $url in the bucket's CORS rule now (runs aws\setup-s3.ps1 with an admin AWS identity)?" $true)) {
    $adminProfile = Select-AwsProfile "" "changing the S3 bucket's CORS / lifecycle rules" "awsProfileS3Admin"
    & (Join-Path $CodeRoot "aws\setup-s3.ps1") -Bucket $storageBucket -RootFolder $storageRoot -Region $storageRegion `
        -AdminProfile $adminProfile -AllowedOrigins @($url) -SkipAppUser
}

Write-Host ""
Write-Host "Nadlan is installed: $url" -ForegroundColor Green
Write-Host "Next:"
Write-Host "  - Your local data: 5-copy-local-db.ps1      - code changes: 6-update-server.ps1"
Write-Host "  - Google sign-in, email (SMTP), status, logs, backups: 7-server-admin.ps1"
if ($Domain) { Write-Host "  - Google sign-in redirect URI to register: $url/signin-google" }
Write-Host "  - Importer packages for this server: 1-build-importer.ps1 (address $url)"

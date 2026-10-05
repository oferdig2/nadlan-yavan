<#
.SYNOPSIS
Copies your local Nadlan database to the server (replaces the server's data; keeps the server's settings).

.DESCRIPTION
For moving your own data (parcels, assets, contacts, portfolios, files' records, history) onto an installed server:
  1. dumps the local MySQL database with mysqldump (consistent snapshot; your local app may keep running),
     leaving out app_config and all sign-in data (users, roles, grants, API tokens, reset links, cookie-signing keys):
     the server keeps its own settings and its own users - a copy of your PC's users and keys would let anyone with your
     local database sign in to the server
  2. uploads it and runs release\server\restore.sh --keep-server-identity on the server: safety dump of the server's data,
     replace, bring the schema up to the deployed release, restart, health check
  3. optionally copies the S3 files those records point to, when the server uses another bucket/folder than your PC
Your local schema must be at the deployed release's version: deploy (6-update-server.ps1) or update locally (update-db.ps1) first.
Rows store file keys relative to the storage folder, so copying the files with aws s3 sync is all files need.
#>
param(
    [string]$SshHost = "",
    [string]$SshUser = "",
    [string]$SshKeyPath = ""
)
. (Join-Path $PSScriptRoot "lib\common.ps1")

# ------------------------------------------------------------------ local MySQL tools
function Find-MySqlTool([string]$Name) {
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $found = Get-ChildItem "$env:ProgramFiles\MySQL\*\bin\$Name.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
    if ($found) { return $found.FullName }
    throw "$Name.exe not found (looked in PATH and $env:ProgramFiles\MySQL\*\bin)."
}
$mysqldump = Find-MySqlTool "mysqldump"
$mysql = Find-MySqlTool "mysql"

$target = Get-SshTarget $SshHost $SshUser $SshKeyPath

Write-Step "Local database"
$dbHost = Read-Value "Local MySQL host" -Setting "localDbHost" -Default "localhost"
$dbPort = Read-Value "Local MySQL port" -Setting "localDbPort" -Default "3306" -Pattern '^\d+$'
$dbUser = Read-Value "Local MySQL user" -Setting "localDbUser" -Default "root"
$dbName = Read-Value "Local database" -Setting "localDbName" -Default "nadlanyavan" -Pattern '^[A-Za-z0-9_]+$'
$dbPassword = Read-Secret "Local MySQL password for $dbUser"

# The password goes to the MySQL tools in an option file, never on the command line.
$quote = if ($dbPassword.Contains('"')) { "'" } else { '"' }
$cnf = Join-Path $env:TEMP ("nadlan-local-" + [guid]::NewGuid().ToString("N") + ".cnf")
$dumpSql = Join-Path $env:TEMP ("nadlan-local-" + [guid]::NewGuid().ToString("N") + ".sql")
$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
$dumpGz = Join-Path $DistRoot "db\$dbName-local-$stamp.sql.gz"

function Invoke-LocalSql([string]$Sql) {
    $r = Invoke-Native $mysql @("--defaults-file=$cnf", "-N", "-B", "-e", $Sql, $dbName)
    if (-not $r.Ok) { throw "Local MySQL: $($r.Err)" }
    return $r.Out.Trim()
}

try {
    [IO.File]::WriteAllText($cnf, "[client]`nhost=$dbHost`nport=$dbPort`nuser=$dbUser`npassword=$quote$($dbPassword.Replace('\', '\\'))$quote`n", (New-Object System.Text.UTF8Encoding($false)))

    $localVersion = [int](Invoke-LocalSql "SELECT COALESCE(MAX(version), 0) FROM schema_version")
    $counts = Invoke-LocalSql "SELECT CONCAT((SELECT COUNT(*) FROM parcel), ' parcels, ', (SELECT COUNT(*) FROM app_user), ' users')"
    $localBucket = Invoke-LocalSql 'SELECT COALESCE(JSON_UNQUOTE(JSON_EXTRACT(json_text, ''$.Nadlan.Storage.Bucket'')), '''') FROM app_config WHERE config_key = ''ms:host'''
    $localRoot = (Invoke-LocalSql 'SELECT COALESCE(JSON_UNQUOTE(JSON_EXTRACT(json_text, ''$.Nadlan.Storage.RootFolder'')), '''') FROM app_config WHERE config_key = ''ms:host''').Trim('/')
    Write-Host "Local '$dbName': schema version $localVersion, $counts; files in s3://$localBucket/$localRoot"

    Write-Step "Server"
    $status = Invoke-Native "ssh" (@(Get-SshOptions $target) + @($target.Address, "sudo nadlan-db status"))
    Write-Host $status.Out.Trim()
    $latestMatch = [regex]::Match($status.Out, 'of (\d+)')
    if (-not $latestMatch.Success) { throw "Couldn't read the server's schema status. Is Nadlan installed there (4-install-server.ps1)? $($status.Err)" }
    $serverLatest = [int]$latestMatch.Groups[1].Value
    if ($localVersion -ne $serverLatest) {
        $fix = if ($localVersion -gt $serverLatest) { "Deploy your current code first: 6-update-server.ps1" } else { "Update your local database first: update-db.ps1" }
        throw "Your local schema (version $localVersion) differs from the release on the server (version $serverLatest). $fix"
    }
    $serverConfig = (Invoke-Native "ssh" (@(Get-SshOptions $target) + @($target.Address, "sudo nadlan-db config show ms:host"))).Out | ConvertFrom-Json
    $serverBucket = "$($serverConfig.Nadlan.Storage.Bucket)"
    $serverRoot = "$($serverConfig.Nadlan.Storage.RootFolder)".Trim('/')

    Write-Host ""
    Write-Host "This REPLACES all data on $($target.Host) with your local '$dbName' ($counts)." -ForegroundColor Yellow
    Write-Host "The server's settings (app_config) are kept, and its current data is dumped to /var/backups/nadlan first." -ForegroundColor Yellow
    $typed = "$(Read-Host "Type the server address ($($target.Host)) to confirm")"
    if ($typed.Trim() -ne $target.Host) { throw "Not confirmed; nothing changed." }

    # Never copied: the server's own settings and sign-in data (restore.sh --keep-server-identity keeps them there).
    $serverOwnedTables = @("app_config", "security_role", "permission", "role_permission", "app_user", "resource_access",
        "api_token", "password_token", "data_protection_key")
    Write-Step "Dumping local '$dbName'"
    $r = Invoke-Native $mysqldump (@("--defaults-file=$cnf", "--single-transaction", "--quick", "--no-tablespaces", "--routines",
        "--triggers", "--hex-blob", "--set-gtid-purged=OFF", "--default-character-set=utf8mb4") +
        ($serverOwnedTables | ForEach-Object { "--ignore-table=$dbName.$_" }) + @("--result-file=$dumpSql", $dbName))
    if (-not $r.Ok) { throw "mysqldump failed: $($r.Err)" }
    New-Item -ItemType Directory -Force (Split-Path $dumpGz) | Out-Null
    $in = [IO.File]::OpenRead($dumpSql)
    try {
        $out = [IO.File]::Create($dumpGz)
        try {
            $gz = New-Object IO.Compression.GZipStream($out, [IO.Compression.CompressionLevel]::Optimal)
            try { $in.CopyTo($gz) } finally { $gz.Dispose() }
        }
        finally { $out.Dispose() }
    }
    finally { $in.Dispose() }
    Write-Host ("{0} ({1:N1} MB)" -f $dumpGz, ((Get-Item $dumpGz).Length / 1MB))
}
finally {
    Remove-Item $cnf, $dumpSql -ErrorAction SilentlyContinue
}

Write-Step "Uploading and restoring on $($target.Host)"
$remoteFile = "/home/$($target.User)/nadlan-upload/$(Split-Path -Leaf $dumpGz)"
try {
    Invoke-Remote $target "rm -rf ~/nadlan-upload && mkdir -p ~/nadlan-upload"
    Copy-ToRemote $target $dumpGz "nadlan-upload/"
    Invoke-RemoteDetached $target "sudo /opt/nadlan/current/server/restore.sh $remoteFile --yes --keep-server-identity" # survives a dropped SSH connection
}
finally {
    try { Invoke-Remote $target "rm -rf ~/nadlan-upload" } catch { }
}
Write-Host "Database copied. The dump is kept at $dumpGz (delete it when no longer needed: it holds personal data)." -ForegroundColor Green

# ------------------------------------------------------------------ files
if ($localBucket -and $serverBucket -and ("$localBucket/$localRoot" -ne "$serverBucket/$serverRoot")) {
    Write-Step "Files"
    $source = "s3://$localBucket/$localRoot".TrimEnd('/')
    $dest = "s3://$serverBucket/$serverRoot".TrimEnd('/')
    Write-Host "The records point to files under $source; the server reads them from $dest."
    if (Read-YesNo "Copy the files now (aws s3 sync, only what's missing)?" $true) {
        $copyProfile = Select-AwsProfile "" "copying files from $source to $dest (read the first, write the second)" "awsProfileS3Copy"
        $old = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try { & aws s3 sync "$source/" "$dest/" --profile $copyProfile --only-show-errors | Out-Host }
        finally { $ErrorActionPreference = $old }
        if ($LASTEXITCODE -ne 0) { throw "aws s3 sync failed (exit $LASTEXITCODE). Re-run it: aws s3 sync $source/ $dest/ --profile $copyProfile" }
        Write-Host "Files copied." -ForegroundColor Green
    }
    else {
        Write-Host "Later: aws s3 sync $source/ $dest/ --profile <profile>"
    }
}
elseif ($localBucket -and -not $serverBucket) {
    Write-Warning "The server has no S3 bucket configured, so attached files won't open there. Set Nadlan:Storage:* with 7-server-admin.ps1."
}

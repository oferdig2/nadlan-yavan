<#
.SYNOPSIS
Updates the server to the current code: build, upload, back up DB, migrate, switch, health check, auto-rollback.

.DESCRIPTION
Builds a new server package from this PC's code (tests first, unless -SkipTests) or reuses the newest one in
code\dist\server, uploads it and runs release\server\deploy.sh on the server:
  stop app -> dump the database (/var/backups/nadlan/...-before-<release>.sql.gz) -> apply new schema scripts
  -> switch /opt/nadlan/current to the new release -> start -> wait until /api/health reports the new release.
If the new release doesn't become healthy within 2 minutes, the previous release is started again (the schema stays
updated; older releases run on it). Downtime is usually 10-30 seconds. The last 3 releases are kept on the server
(7-server-admin.ps1 can switch back to one). Server settings and data are never touched by an update.
#>
param(
    [string]$SshHost = "",
    [string]$SshUser = "",
    [string]$SshKeyPath = "",
    [switch]$SkipTests
)
. (Join-Path $PSScriptRoot "lib\common.ps1")

$target = Get-SshTarget $SshHost $SshUser $SshKeyPath
$running = (Invoke-Native "ssh" (@(Get-SshOptions $target) + @($target.Address, "curl -fsS --max-time 5 http://127.0.0.1:5515/api/health"))).Out.Trim()
Write-Host "Running now: $(if ($running) { $running } else { 'no answer' })"

$package = Get-ServerPackage $false (-not $SkipTests)
$release = (Split-Path -Leaf $package) -replace '^nadlan-server-(.+)\.tar\.gz$', '$1'
if (-not (Read-YesNo "Deploy release $release to $($target.Host)? (the site is down for ~10-30 s)" $true)) { throw "Cancelled." }

try {
    $remoteDir = Send-ServerPackage $target $package
    Write-Step "Deploying $release"
    Invoke-RemoteDetached $target "sudo bash $remoteDir/server/deploy.sh $remoteDir" # survives a dropped SSH connection
}
finally {
    try { Invoke-Remote $target "rm -rf ~/nadlan-upload" } catch { Write-Warning "Couldn't remove ~/nadlan-upload on the server." }
}

Write-Host ""
Write-Host "Release $release is live on $($target.Host)." -ForegroundColor Green
$url = Get-Setting "publicUrl"
if ($url) { Write-Host "Open: $url" }

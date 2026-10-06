<#
.SYNOPSIS
Day-to-day server admin: status, logs, settings (Google sign-in, SMTP, Maps), passwords, backups, rollback.

.DESCRIPTION
A menu of server tasks over SSH. Nothing is compiled or edited on the server; settings live in the app_config table
and are changed through the DB tool (sudo nadlan-db). Secret values (passwords, client secrets) are sent over SSH's
stdin, never on a command line.
#>
param(
    [string]$SshHost = "",
    [string]$SshUser = "",
    [string]$SshKeyPath = ""
)
. (Join-Path $PSScriptRoot "lib\common.ps1")

$target = Get-SshTarget $SshHost $SshUser $SshKeyPath
$server = "/opt/nadlan/current/server"

function Get-RemoteLines([string]$Command) {
    $r = Invoke-Native "ssh" (@(Get-SshOptions $target) + @($target.Address, $Command))
    if (-not $r.Ok) { throw "Remote command failed: $($r.Err)" }
    return @($r.Out -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Select-FromList([string[]]$Items, [string]$Prompt) {
    for ($i = 0; $i -lt $Items.Count; $i++) { Write-Host ("  [{0}] {1}" -f ($i + 1), $Items[$i]) }
    $answer = "$(Read-Host "$Prompt (number, Enter = cancel)")".Trim()
    if ($answer -match '^\d+$' -and [int]$answer -ge 1 -and [int]$answer -le $Items.Count) { return $Items[[int]$answer - 1] }
    return ""
}

function Restart-App {
    Invoke-Remote $target "sudo systemctl restart nadlan && for i in `$(seq 1 30); do curl -fsS --max-time 3 http://127.0.0.1:5515/api/health 2>/dev/null && exit 0; sleep 2; done; echo 'No healthy answer after 60 s - see the log.'; exit 1"
}

function Set-RemoteConfig([string]$Path, [string]$Value) {
    Invoke-RemoteWithInput $target "sudo $server/config-set.sh ms:host $Path" $Value
}

# Common settings: path, prompt, secret?
$knownSettings = @(
    @("Nadlan:Maps:GoogleApiKey", "Google Maps JavaScript API key", $false),
    @("Nadlan:Auth:Google:ClientId", "Google sign-in OAuth client id", $false),
    @("Nadlan:Auth:Google:ClientSecret", "Google sign-in OAuth client secret", $true),
    @("Nadlan:Auth:PublicBaseUrl", "Public address used in emailed links (https://...)", $false),
    @("Nadlan:Auth:Email:SmtpHost", "SMTP host (e.g. email-smtp.eu-central-1.amazonaws.com)", $false),
    @("Nadlan:Auth:Email:SmtpPort", "SMTP port (587)", $false),
    @("Nadlan:Auth:Email:SmtpUser", "SMTP user", $false),
    @("Nadlan:Auth:Email:SmtpPassword", "SMTP password", $true),
    @("Nadlan:Auth:Email:From", "Sender address of emails", $false),
    @("Nadlan:Storage:Bucket", "S3 bucket for files", $false),
    @("Nadlan:Storage:RootFolder", "Folder in the bucket (e.g. nadlan/prod)", $false),
    @("Nadlan:Storage:Region", "Bucket region", $false),
    @("Nadlan:Storage:Delivery:PrivateKeyPem", "CloudFront signing private key (one line, \n for line breaks)", $true)
)

$actions = @(
    "Status report",
    "App log (last 200 lines)",
    "Restart the app",
    "Show settings (app_config ms:host - includes secrets)",
    "Change a setting",
    "List users",
    "Set a user's password",
    "Back up the database now",
    "Download the newest backup to this PC",
    "Restore a backup",
    "Switch back to an earlier release",
    "Open a shell on the server"
)

while ($true) {
    Write-Host ""
    Write-Host "Server $($target.Host)" -ForegroundColor Cyan
    $action = Select-FromList $actions "Task"
    if (-not $action) { return }
    try {
        switch ($action) {
            "Status report" { Invoke-Remote $target "sudo $server/status.sh" }
            "App log (last 200 lines)" { Invoke-Remote $target "sudo journalctl -u nadlan -n 200 --no-pager" }
            "Restart the app" { Restart-App }
            "Show settings (app_config ms:host - includes secrets)" { Invoke-Remote $target "sudo nadlan-db config show ms:host" }
            "Change a setting" {
                $labels = @($knownSettings | ForEach-Object { "{0,-42} {1}" -f $_[0], $_[1] }) + @("(another path)")
                $choice = Select-FromList $labels "Setting"
                if (-not $choice) { continue }
                $index = [array]::IndexOf($labels, $choice)
                if ($index -lt $knownSettings.Count) {
                    $path = $knownSettings[$index][0]
                    $secret = $knownSettings[$index][2]
                }
                else {
                    $path = Read-Value "Config path" -Pattern '^Nadlan(:[A-Za-z0-9]+)+$' -PatternHint "Like Nadlan:Maps:DefaultZoom"
                    $secret = Read-YesNo "Is the value a secret (typed hidden)?" $false
                }
                $value = if ($secret) { Read-Secret "New value for $path (Enter = empty)" -MinLength 0 } else { Read-Host "New value for $path (Enter = empty)" }
                Set-RemoteConfig $path $value
                if (Read-YesNo "Restart the app now to apply it?" $true) { Restart-App }
            }
            "List users" { Invoke-Remote $target "sudo nadlan-db user list" }
            "Set a user's password" {
                $email = Read-Value "User email" -Pattern '^[^\s''"`;|&<>()$\\]+@[^\s''"`;|&<>()$\\]+$' -PatternHint "An email address."
                $password = Read-Secret "New password (at least 8 characters)" -MinLength 8 -Confirm
                Invoke-RemoteWithInput $target "sudo $server/set-password.sh $email" $password
            }
            "Back up the database now" { Invoke-Remote $target "sudo $server/backup.sh manual" }
            "Download the newest backup to this PC" {
                $newest = @(Get-RemoteLines "sudo ls -1t /var/backups/nadlan")[0]
                if (-not $newest) { Write-Host "No backups yet."; continue }
                $localDir = Join-Path $DistRoot "backups"
                New-Item -ItemType Directory -Force $localDir | Out-Null
                Invoke-Remote $target "sudo install -m 600 -o $($target.User) /var/backups/nadlan/$newest ~/$newest"
                try {
                    $old = $ErrorActionPreference; $ErrorActionPreference = "Continue"
                    & scp @(Get-SshOptions $target) "$($target.Address):$newest" (Join-Path $localDir $newest) | Out-Host
                    $ErrorActionPreference = $old
                    if ($LASTEXITCODE -ne 0) { throw "Download failed." }
                }
                finally { Invoke-Remote $target "rm -f ~/$newest" }
                Write-Host "Saved $(Join-Path $localDir $newest) (personal data: keep it safe)." -ForegroundColor Green
            }
            "Restore a backup" {
                $files = Get-RemoteLines "sudo ls -1t /var/backups/nadlan"
                if ($files.Count -eq 0) { Write-Host "No backups yet."; continue }
                $file = Select-FromList ($files | Select-Object -First 20) "Backup to restore"
                if (-not $file) { continue }
                $keep = Read-YesNo "Keep the server's current settings (app_config) instead of the ones in the backup?" $true
                Write-Host "This replaces ALL data with $file (the current data is dumped first)." -ForegroundColor Yellow
                if ("$(Read-Host "Type RESTORE to continue")".Trim() -cne "RESTORE") { Write-Host "Cancelled."; continue }
                Invoke-RemoteDetached $target "sudo $server/restore.sh /var/backups/nadlan/$file --yes$(if ($keep) { ' --keep-config' })"
            }
            "Switch back to an earlier release" {
                $current = @(Get-RemoteLines "basename `$(readlink -f /opt/nadlan/current)")[0]
                $releases = @(Get-RemoteLines "ls -1t /opt/nadlan/releases" | Where-Object { $_ -ne $current })
                Write-Host "Current: $current"
                if ($releases.Count -eq 0) { Write-Host "No other release on the server."; continue }
                $release = Select-FromList $releases "Release to run"
                if (-not $release) { continue }
                Invoke-RemoteDetached $target "sudo $server/rollback.sh $release"
            }
            "Open a shell on the server" {
                Write-Host "Type 'exit' to come back." -ForegroundColor DarkGray
                & ssh @(Get-SshOptions $target) $target.Address
            }
        }
    }
    catch {
        Write-Host $_.Exception.Message -ForegroundColor Red
    }
}

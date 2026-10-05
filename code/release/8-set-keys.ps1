<#
.SYNOPSIS
Puts the outside service keys on the server - Google Maps, Google sign-in, email (SMTP) - first setup or rotation.

.DESCRIPTION
Shows which keys the server has (masked) and which are missing, asks for the missing ones, and optionally replaces
ones already set (key rotation). Values go to the server's app_config over SSH's stdin - never on a command line, never
saved on this PC - then the app restarts and its health is checked. Enter keeps the current value; "-" clears it.
Afterwards it prints what to register in Google Cloud for this server's address.
#>
param(
    [string]$SshHost = "",
    [string]$SshUser = "",
    [string]$SshKeyPath = ""
)
. (Join-Path $PSScriptRoot "lib\common.ps1")

$target = Get-SshTarget $SshHost $SshUser $SshKeyPath
$server = "/opt/nadlan/current/server"

# Group, config path, label, secret?, format check (regex; a mismatch only warns), hint
$keys = @(
    @{ Group = "Google Maps"; Path = "Nadlan:Maps:GoogleApiKey"; Label = "Maps JavaScript API key"; Secret = $false
       Check = '^AIza[0-9A-Za-z_-]{35}$'; Hint = "Google Cloud > APIs & Services > Credentials; starts with AIza" },
    @{ Group = "Google sign-in"; Path = "Nadlan:Auth:Google:ClientId"; Label = "OAuth client ID"; Secret = $false
       Check = '\.apps\.googleusercontent\.com$'; Hint = "ends with .apps.googleusercontent.com" },
    @{ Group = "Google sign-in"; Path = "Nadlan:Auth:Google:ClientSecret"; Label = "OAuth client secret"; Secret = $true
       Check = '^GOCSPX-'; Hint = "usually starts with GOCSPX-" },
    @{ Group = "Email (SMTP)"; Path = "Nadlan:Auth:Email:SmtpHost"; Label = "SMTP host"; Secret = $false
       Check = '^[A-Za-z0-9.-]+$'; Hint = "e.g. email-smtp.eu-north-1.amazonaws.com or smtp.gmail.com" },
    @{ Group = "Email (SMTP)"; Path = "Nadlan:Auth:Email:SmtpPort"; Label = "SMTP port"; Secret = $false
       Check = '^\d{2,5}$'; Hint = "usually 587" },
    @{ Group = "Email (SMTP)"; Path = "Nadlan:Auth:Email:SmtpUser"; Label = "SMTP user"; Secret = $false; Check = ''; Hint = "" },
    @{ Group = "Email (SMTP)"; Path = "Nadlan:Auth:Email:SmtpPassword"; Label = "SMTP password"; Secret = $true; Check = ''; Hint = "" },
    @{ Group = "Email (SMTP)"; Path = "Nadlan:Auth:Email:From"; Label = "Sender address"; Secret = $false
       Check = '^[^\s@]+@[^\s@]+$'; Hint = "e.g. nadlan@yourcompany.com" }
)

function Get-RemoteConfig {
    $r = Invoke-Native "ssh" (@(Get-SshOptions $target) + @($target.Address, "sudo nadlan-db config show ms:host"))
    if (-not $r.Ok) { throw "Could not read the server's settings: $($r.Err)" }
    return ($r.Out | ConvertFrom-Json)
}

function Get-ConfigValue($Config, [string]$Path) {
    $node = $Config
    foreach ($part in $Path.Split(':')) {
        if ($null -eq $node -or -not $node.PSObject.Properties[$part]) { return "" }
        $node = $node.$part
    }
    return "$node"
}

function Format-Current([string]$Value, [bool]$Secret) {
    if (-not $Value) { return "missing" }
    if ($Secret) { return "set ($($Value.Length) characters)" }
    if ($Value.Length -le 12) { return "set: $Value" }
    return "set: $($Value.Substring(0, 6))...$($Value.Substring($Value.Length - 4))"
}

function Read-Key($Key, [string]$Current) {
    if ($Key.Hint) { Write-Host "  ($($Key.Hint))" -ForegroundColor DarkGray }
    while ($true) {
        $prompt = "  $($Key.Group) - $($Key.Label) [Enter = $(if ($Current) { 'keep' } else { 'leave empty' }), - = clear]"
        $value = if ($Key.Secret) { Read-Secret $prompt -MinLength 0 } else { "$(Read-Host $prompt)" }
        $value = $value.Trim()
        if (-not $value) { return $null }
        if ($value -eq "-") { return "" }
        if ($Key.Check -and $value -notmatch $Key.Check) {
            if (-not (Read-YesNo "  That doesn't look like a $($Key.Label) ($($Key.Hint)). Use it anyway?" $false)) { continue }
        }
        return $value
    }
}

Write-Step "Keys on $($target.Host)"
$config = Get-RemoteConfig
$baseUrl = (Get-ConfigValue $config "Nadlan:Auth:PublicBaseUrl").TrimEnd('/')
if (-not $baseUrl) { $baseUrl = "http://$($target.Host)" }
$current = @{}
foreach ($k in $keys) {
    $current[$k.Path] = Get-ConfigValue $config $k.Path
    Write-Host ("  {0,-15} {1,-24} {2}" -f $k.Group, $k.Label, (Format-Current $current[$k.Path] $k.Secret))
}

# Missing keys first; then, for rotation, the groups that are already set.
$changes = [ordered]@{}
$missing = @($keys | Where-Object { -not $current[$_.Path] })
if ($missing.Count -gt 0) {
    Write-Step "Missing keys (Enter skips one; email is only needed for password-reset mails)"
    foreach ($k in $missing) {
        $value = Read-Key $k ""
        if ($null -ne $value) { $changes[$k.Path] = $value }
    }
}

$setGroups = @($keys | Where-Object { $current[$_.Path] } | ForEach-Object { $_.Group } | Select-Object -Unique)
foreach ($group in $setGroups) {
    if (-not (Read-YesNo "Replace the $group keys that are already set (rotation)?" $false)) { continue }
    foreach ($k in @($keys | Where-Object { $_.Group -eq $group -and $current[$_.Path] -and -not $changes.Contains($_.Path) })) {
        $value = Read-Key $k $current[$k.Path]
        if ($null -ne $value) { $changes[$k.Path] = $value }
    }
}

if ($changes.Count -eq 0) {
    Write-Host ""
    Write-Host "Nothing to change."
    return
}

Write-Step "To save on the server"
foreach ($path in $changes.Keys) {
    $k = $keys | Where-Object { $_.Path -eq $path }
    $shown = if ($changes[$path] -eq "") { "(cleared)" } else { Format-Current $changes[$path] $k.Secret }
    Write-Host ("  {0,-15} {1,-24} {2}" -f $k.Group, $k.Label, $shown)
}
if (-not (Read-YesNo "Save these and restart the app?" $true)) {
    Write-Host "Nothing saved."
    return
}

foreach ($path in $changes.Keys) {
    Invoke-RemoteWithInput $target "sudo $server/config-set.sh ms:host $path" $changes[$path]
}
Write-Host "Saved."

Write-Step "Restarting the app"
Invoke-Remote $target "sudo systemctl restart nadlan && for i in `$(seq 1 30); do curl -fsS --max-time 3 http://127.0.0.1:5515/api/health >/dev/null 2>&1 && echo 'Healthy.' && exit 0; sleep 2; done; echo 'No healthy answer after 60 s - see: journalctl -u nadlan -n 100'; exit 1"

Write-Step "Register this server in Google Cloud (APIs & Services > Credentials)"
Write-Host "  Maps API key  > Application restrictions > Websites:  $baseUrl/*"
Write-Host "  OAuth client  > Authorized JavaScript origins:         $baseUrl"
Write-Host "  OAuth client  > Authorized redirect URIs:              $baseUrl/signin-google"
if ($baseUrl -notmatch '^https://') {
    Write-Host ""
    Write-Warning "Google sign-in needs an https address with a domain name; Google refuses redirect URIs like $baseUrl. The keys are stored now and work once the domain is set up (re-run 4-install-server.ps1 with the domain). Maps works on $baseUrl already."
}

<#
.SYNOPSIS
Sets the sign-in passwords of the server's Admins (e.g. after the install, or to reset a forgotten one).

.DESCRIPTION
Lists the users with the Admin role on the server and whether each has a password, then asks, Admin by Admin,
whether to set one (default: yes for an Admin without a password). The password is typed hidden, twice, and goes to
the server over SSH's stdin (set-password.sh) - never on a command line, never saved on this PC. Setting it signs that
Admin's other sessions out and unlocks the account. Admins with Google sign-in configured don't need one.
#>
param(
    [string]$SshHost = "",
    [string]$SshUser = "",
    [string]$SshKeyPath = ""
)
. (Join-Path $PSScriptRoot "lib\common.ps1")

$target = Get-SshTarget $SshHost $SshUser $SshKeyPath
$server = "/opt/nadlan/current/server"

# "email  ROLE  active|DISABLED  password: yes|no  last login: ..." (Nadlan.DbTool user list)
$r = Invoke-Native "ssh" (@(Get-SshOptions $target) + @($target.Address, "sudo nadlan-db user list"))
if (-not $r.Ok) { throw "Could not list the users: $($r.Err)" }
$admins = @($r.Out -split "`n" | ForEach-Object {
        $m = [regex]::Match($_, '^(?<email>\S+@\S+)\s+ADMIN\s+(?<state>\S+)\s+password:\s+(?<pw>yes|no)')
        if ($m.Success) { [pscustomobject]@{ Email = $m.Groups["email"].Value; State = $m.Groups["state"].Value; HasPassword = $m.Groups["pw"].Value -eq "yes" } }
    })
if ($admins.Count -eq 0) { throw "No Admin users on $($target.Host). (Is the app installed? 4-install-server.ps1)" }

Write-Step "Admins on $($target.Host)"
foreach ($a in $admins) {
    Write-Host ("  {0,-36} {1,-9} password: {2}" -f $a.Email, $a.State, $(if ($a.HasPassword) { "set" } else { "none" }))
}

$changed = 0
foreach ($a in $admins) {
    Write-Host ""
    $question = if ($a.HasPassword) { "Replace the password of $($a.Email)?" } else { "Set a password for $($a.Email)?" }
    if (-not (Read-YesNo $question (-not $a.HasPassword))) { continue }
    $password = Read-Secret "Password for $($a.Email) (at least 8 characters)" -MinLength 8 -Confirm
    Invoke-RemoteWithInput $target "sudo $server/set-password.sh $($a.Email)" $password
    $changed++
}

Write-Host ""
if ($changed -gt 0) {
    Write-Host "Done. They sign in at http://$($target.Host)/ (or the site's domain) with their email and the new password." -ForegroundColor Green
}
else {
    Write-Host "Nothing changed."
}

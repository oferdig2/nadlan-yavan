<#
.SYNOPSIS
Creates the MySQL user nadlan_admin (full rights) for MySQL Workbench, or changes its password - you type it.

.DESCRIPTION
Your own MySQL login on the server, separate from the app's user (nadlan) and from root, which are not touched.
The password is typed hidden, twice, and goes to the server over SSH's stdin only - never on a command line, never in
a file. Re-run it to change the password. MySQL itself stays closed to the internet: Workbench connects through SSH
with the server key ("Standard TCP/IP over SSH"); the settings are printed at the end.
#>
param(
    [string]$SshHost = "",
    [string]$SshUser = "",
    [string]$SshKeyPath = ""
)
. (Join-Path $PSScriptRoot "lib\common.ps1")

$target = Get-SshTarget $SshHost $SshUser $SshKeyPath
$mysqlUser = "nadlan_admin"

Write-Step "MySQL user $mysqlUser on $($target.Host)"
Write-Host "MySQL's password rules: at least 8 characters, with upper- and lowercase letters, a digit and a special character."
$password = Read-Secret "Password for $mysqlUser" -MinLength 8 -Confirm

# One quoted SQL string: escape the backslash and the quote; the text reaches mysql on stdin, not on a command line.
$sqlPassword = $password.Replace('\', '\\').Replace("'", "''")
# 127.0.0.1 = Workbench through the SSH tunnel; localhost = "mysql -u nadlan_admin -p" in a shell on the server.
$sql = foreach ($sqlHost in "127.0.0.1", "localhost") {
    "CREATE USER IF NOT EXISTS '$mysqlUser'@'$sqlHost' IDENTIFIED BY '$sqlPassword';"
    "ALTER USER '$mysqlUser'@'$sqlHost' IDENTIFIED BY '$sqlPassword' ACCOUNT UNLOCK;"
    "GRANT ALL PRIVILEGES ON *.* TO '$mysqlUser'@'$sqlHost' WITH GRANT OPTION;"
}
Invoke-RemoteWithInput $target "sudo mysql --defaults-extra-file=/root/.my.cnf" ($sql -join "`n")
Write-Host "Password set for $mysqlUser (full rights). The app's user (nadlan) and root are unchanged." -ForegroundColor Green

Write-Host ""
Write-Host "MySQL Workbench > + (new connection) > Connection Method: Standard TCP/IP over SSH" -ForegroundColor Cyan
Write-Host "  SSH Hostname:    $($target.Host):22"
Write-Host "  SSH Username:    $($target.User)"
Write-Host "  SSH Key File:    $($target.Key)"
Write-Host "  MySQL Hostname:  127.0.0.1      Port: 3306"
Write-Host "  Username:        $mysqlUser"
Write-Host "  Password:        the one you just typed (Store in Vault...)"
Write-Host "  Default Schema:  nadlanyavan"
Write-Host "SSH only works from an IP the server's firewall allows. Settings edited in app_config apply after a restart of the app (7-server-admin.ps1)." -ForegroundColor DarkGray

# Shared helpers for the release scripts (dot-sourced). Windows PowerShell 5.1 compatible; keep this file ASCII.
# - prompts that remember the last answer in release\release.local.json (git-ignored, never holds secrets)
# - AWS identity (profile) selection, SSH/SCP to the server, building the Linux server package
$ErrorActionPreference = "Stop"

$ReleaseRoot = Split-Path -Parent $PSScriptRoot
$CodeRoot = Split-Path -Parent $ReleaseRoot
$DistRoot = Join-Path $CodeRoot "dist"
$SettingsPath = Join-Path $ReleaseRoot "release.local.json"
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

# ------------------------------------------------------------------ console

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Read-YesNo([string]$Prompt, [bool]$Default = $true) {
    $hint = if ($Default) { "(Y/n)" } else { "(y/N)" }
    while ($true) {
        $answer = "$(Read-Host "$Prompt $hint")".Trim().ToLowerInvariant()
        if (-not $answer) { return $Default }
        if ($answer -match '^(y|yes)$') { return $true }
        if ($answer -match '^(n|no)$') { return $false }
        Write-Host "Please answer y or n." -ForegroundColor Yellow
    }
}

function Read-Secret([string]$Prompt, [int]$MinLength = 1, [switch]$Confirm) {
    while ($true) {
        $first = ConvertFrom-SecureText (Read-Host -Prompt $Prompt -AsSecureString)
        if ($first.Length -lt $MinLength) { Write-Host "At least $MinLength characters." -ForegroundColor Yellow; continue }
        if (-not $Confirm) { return $first }
        $second = ConvertFrom-SecureText (Read-Host -Prompt "Repeat it" -AsSecureString)
        if ($first -eq $second) { return $first }
        Write-Host "The two entries differ; try again." -ForegroundColor Yellow
    }
}

function ConvertFrom-SecureText([Security.SecureString]$Secure) {
    if ($null -eq $Secure) { return "" }
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# ------------------------------------------------------------------ remembered answers

function Get-Settings {
    if (Test-Path -LiteralPath $SettingsPath) {
        try { return (Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json) }
        catch { Write-Warning "Ignoring unreadable $SettingsPath ($($_.Exception.Message))." }
    }
    return New-Object PSObject
}

function Get-Setting([string]$Name) {
    $p = (Get-Settings).PSObject.Properties[$Name]
    if ($p -and $null -ne $p.Value) { return [string]$p.Value }
    return ""
}

function Set-Setting([string]$Name, [string]$Value) {
    $s = Get-Settings
    if ($s.PSObject.Properties[$Name]) { $s.$Name = $Value } else { $s | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
    [IO.File]::WriteAllText($SettingsPath, ($s | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
}

# Returns -Value if given (validated), else asks. The suggestion is the remembered answer (-Setting) or -Default.
# -Optional: Enter with no suggestion returns ""; typing "-" clears a remembered answer.
function Read-Value {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [string]$Value = "",
        [string]$Setting = "",
        [string]$Default = "",
        [switch]$Optional,
        [string]$Pattern = "",
        [string]$PatternHint = "Invalid value."
    )
    if ($Value) {
        if ($Pattern -and $Value -notmatch $Pattern) { throw "$Prompt '$Value': $PatternHint" }
        if ($Setting) { Set-Setting $Setting $Value }
        return $Value
    }

    $remembered = if ($Setting) { Get-Setting $Setting } else { "" }
    $suggest = if ($remembered) { $remembered } else { $Default }
    while ($true) {
        $label = $Prompt
        if ($suggest) { $label += " [$suggest]" }
        if ($Optional) { $label += $(if ($suggest) { " ('-' = none)" } else { " (Enter = none)" }) }
        $answer = "$(Read-Host $label)".Trim()
        if ($answer -eq "-" -and $Optional) { $answer = ""; $suggest = "" }
        elseif (-not $answer) { $answer = $suggest }

        if (-not $answer) {
            if ($Optional) { if ($Setting) { Set-Setting $Setting "" }; return "" }
            Write-Host "Required." -ForegroundColor Yellow
            continue
        }
        if ($Pattern -and $answer -notmatch $Pattern) { Write-Host $PatternHint -ForegroundColor Yellow; continue }
        if ($Setting) { Set-Setting $Setting $answer }
        return $answer
    }
}

# ------------------------------------------------------------------ tools

function Assert-Tool([string]$Name, [string]$Hint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "'$Name' was not found. $Hint" }
}

# Runs a native program; returns exit code + stdout + stderr without PowerShell 5.1 turning stderr into exceptions.
function Invoke-Native([string]$FilePath, [string[]]$Arguments) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { $output = & $FilePath @Arguments 2>&1 } finally { $ErrorActionPreference = $old }
    $code = $LASTEXITCODE
    $out = @($output | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
    $err = @($output | Where-Object { $_ -is [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
    return [pscustomobject]@{ Ok = ($code -eq 0); Code = $code; Out = $out; Err = $err }
}

# ------------------------------------------------------------------ AWS

function Invoke-Aws([string]$AwsProfile, [string[]]$Arguments, [switch]$AllowFailure) {
    $all = @($Arguments) + @("--profile", $AwsProfile, "--output", "json")
    $r = Invoke-Native "aws" $all
    if (-not $r.Ok -and -not $AllowFailure) { throw "aws $($Arguments -join ' ') failed: $($r.Err)" }
    return $r
}

# Lets the user pick one of the AWS CLI profiles on this PC (different jobs may use different identities), shows
# which account/identity it is, and asks to confirm. -Setting remembers the choice per job.
function Select-AwsProfile([string]$AwsProfile, [string]$Purpose, [string]$Setting) {
    Assert-Tool "aws" "Install AWS CLI v2: https://aws.amazon.com/cli/"
    while ($true) {
        if (-not $AwsProfile) {
            $profiles = @((Invoke-Native "aws" @("configure", "list-profiles")).Out -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            if ($profiles.Count -eq 0) { throw "No AWS profiles on this PC. Create one: aws configure --profile <name>   (or: aws configure sso)" }
            $remembered = Get-Setting $Setting
            Write-Host ""
            Write-Host "AWS identity for: $Purpose" -ForegroundColor Cyan
            for ($i = 0; $i -lt $profiles.Count; $i++) {
                $mark = if ($profiles[$i] -eq $remembered) { "   (last used)" } else { "" }
                Write-Host ("  [{0}] {1}{2}" -f ($i + 1), $profiles[$i], $mark)
            }
            $answer = "$(Read-Host "Profile number or name$(if ($remembered) { " [$remembered]" })")".Trim()
            if (-not $answer) { $answer = $remembered }
            if ($answer -match '^\d+$' -and [int]$answer -ge 1 -and [int]$answer -le $profiles.Count) { $answer = $profiles[[int]$answer - 1] }
            if (-not ($profiles -contains $answer)) { Write-Host "Not one of the profiles above." -ForegroundColor Yellow; continue }
            $AwsProfile = $answer
        }

        $id = Invoke-Aws $AwsProfile @("sts", "get-caller-identity") -AllowFailure
        if (-not $id.Ok) {
            Write-Host "Profile '$AwsProfile' can't sign in: $($id.Err)" -ForegroundColor Red
            if ($id.Err -match "(?i)sso|token") { Write-Host "For an SSO profile run: aws sso login --profile $AwsProfile" -ForegroundColor Yellow }
            $AwsProfile = ""
            if (-not (Read-YesNo "Choose another profile?" $true)) { throw "No usable AWS identity." }
            continue
        }
        $who = $id.Out | ConvertFrom-Json
        Write-Host "  account:  $($who.Account)"
        Write-Host "  identity: $($who.Arn)"
        if (Read-YesNo "Use this identity?" $true) {
            Set-Setting $Setting $AwsProfile
            return $AwsProfile
        }
        $AwsProfile = ""
    }
}

# ------------------------------------------------------------------ SSH

# Asks for (or takes) the server address, user and key, and checks that SSH works.
function Get-SshTarget([string]$SshHost, [string]$SshUser, [string]$SshKeyPath) {
    Assert-Tool "ssh" "Install the Windows OpenSSH client: Settings > System > Optional features > OpenSSH Client."
    $h = Read-Value "Server address (EC2 public IP or DNS name; host:port for another SSH port)" $SshHost -Setting "sshHost" `
        -Pattern '^[A-Za-z0-9.-]+(:\d+)?$' -PatternHint "A host name or IP address, optionally :port."
    $u = Read-Value "SSH user" $SshUser -Setting "sshUser" -Default "ec2-user"
    $k = Read-Value "SSH private key file (.pem)" $SshKeyPath -Setting "sshKeyPath"
    $k = $k.Trim('"')
    if (-not (Test-Path -LiteralPath $k)) { throw "Key file not found: $k" }
    $port = "22"
    if ($h -match '^(.+):(\d+)$') { $h = $Matches[1]; $port = $Matches[2] }
    $target = [pscustomobject]@{ Host = $h; Port = $port; User = $u; Key = (Resolve-Path -LiteralPath $k).Path; Address = "$u@$h" }
    Test-SshTarget $target
    return $target
}

function Get-SshOptions($Target) {
    $port = if ($Target.PSObject.Properties["Port"] -and $Target.Port) { $Target.Port } else { "22" }
    return @("-i", $Target.Key, "-o", "Port=$port", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=15",
        "-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=6", "-o", "LogLevel=ERROR")
}

function Test-SshTarget($Target) {
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        $r = Invoke-Native "ssh" (@(Get-SshOptions $Target) + @("-o", "BatchMode=yes", $Target.Address, "echo ok"))
        if ($r.Ok) { Write-Host "SSH to $($Target.Address): ok" -ForegroundColor DarkGray; return }
        # A dropped connection (exit 255 without a key/permission problem) is often a passing network hiccup.
        if ($attempt -lt 4 -and $r.Code -eq 255 -and $r.Err -notmatch "Permission denied|UNPROTECTED PRIVATE KEY|bad permissions|Could not resolve") {
            Write-Host "SSH to $($Target.Address) dropped$(if ($r.Err) { " ($($r.Err.Trim()))" }); retrying in 5 s..." -ForegroundColor Yellow
            Start-Sleep -Seconds 5
            continue
        }
        if ($r.Err -match "UNPROTECTED PRIVATE KEY|bad permissions") {
            Write-Host "Windows OpenSSH refuses a key file that other accounts can read." -ForegroundColor Yellow
            if (Read-YesNo "Restrict '$($Target.Key)' to your Windows user only?" $true) {
                & icacls $Target.Key /inheritance:r /grant:r "$($env:USERNAME):R" | Out-Null
                continue
            }
        }
        throw "SSH to $($Target.Address) failed: $($r.Err)`nCheck the address, the key, and that the security group allows port 22 from this PC."
    }
}

# Runs a command on the server, showing its output live. Keep double quotes out of $Command (PowerShell 5.1 mangles them).
function Invoke-Remote($Target, [string]$Command) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & ssh @(Get-SshOptions $Target) $Target.Address $Command | Out-Host }
    finally { $ErrorActionPreference = $old }
    if ($LASTEXITCODE -ne 0) { throw "Remote command failed (exit $LASTEXITCODE): $Command" }
}

# Runs a long root operation (deploy, restore, rollback, install) on the server as its own systemd unit, so it finishes
# even if this PC's SSH connection drops halfway - an update cut off between "stop the app" and "start it" used to
# leave the site down. Shows the output live; after a dropped connection it reconnects (for up to 15 minutes) and
# keeps following until the operation ends, then returns its real exit code. $Command must start with "sudo " and
# contain no single quotes. Logs stay in /var/lib/nadlan-ops (30 days).
function Invoke-RemoteDetached($Target, [string]$Command) {
    if (-not $Command.StartsWith("sudo ")) { throw "Invoke-RemoteDetached: the command must start with 'sudo '." }
    $inner = $Command.Substring(5)
    if ($inner.Contains("'")) { throw "Invoke-RemoteDetached: no single quotes in the command." }
    $unit = "nadlan-op-" + (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss") + "-" + (Get-Random -Maximum 10000)
    # No "$" or "%" in what systemd-run starts (systemd may expand them in a unit's command line): success = 0, failure = 1.
    if ($inner -match '[$%]') { throw "Invoke-RemoteDetached: no `$ or % in the command." }
    $start = ('sudo mkdir -p /var/lib/nadlan-ops && sudo systemd-run --unit={0} --quiet --property=TimeoutStartSec=infinity ' +
        '/bin/bash -c ''{1} > /var/lib/nadlan-ops/{0}.log 2>&1 && echo 0 > /var/lib/nadlan-ops/{0}.rc || echo 1 > /var/lib/nadlan-ops/{0}.rc''') -f $unit, $inner
    Invoke-Remote $Target $start

    # Follows the log until the .rc file appears; exits with the operation's code. ssh itself exits 255 when the
    # connection fails, which our scripts never use.
    $follow = ('sudo bash -c ''D=/var/lib/nadlan-ops; U={0}; until [ -f $D/$U.log ]; do sleep 0.2; done; ' +
        'tail -n {1} -f $D/$U.log & T=$!; until [ -f $D/$U.rc ]; do sleep 1; done; sleep 1; kill $T 2>/dev/null; ' +
        'find $D -type f -mtime +30 -delete 2>/dev/null; exit $(cat $D/$U.rc)''')
    $from = "+1"
    $deadline = (Get-Date).AddMinutes(15)
    while ($true) {
        $old = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try { & ssh @(Get-SshOptions $Target) $Target.Address ($follow -f $unit, $from) | Out-Host }
        finally { $ErrorActionPreference = $old }
        $code = $LASTEXITCODE
        if ($code -eq 0) { return }
        if ($code -ne 255) { throw "Remote command failed (exit $code): $Command (log: /var/lib/nadlan-ops/$unit.log)" }
        if ((Get-Date) -gt $deadline) {
            throw "Lost the connection to $($Target.Host). The operation goes on by itself on the server; check later: sudo cat /var/lib/nadlan-ops/$unit.log (exit code in $unit.rc)"
        }
        Write-Warning "Connection to $($Target.Host) lost - the operation continues on the server. Reconnecting in 10 s..."
        Start-Sleep -Seconds 10
        $from = "20" # after a reconnect: the last lines, then live again
    }
}

# Runs a command on the server with $InputText on its stdin (for secrets: never on a command line).
function Invoke-RemoteWithInput($Target, [string]$Command, [string]$InputText) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "ssh"
    $quoted = @(Get-SshOptions $Target) | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    $psi.Arguments = (@($quoted) + @($Target.Address, ('"' + $Command + '"'))) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    # .NET Framework writes the console input encoding's preamble to a child's stdin: without this, a UTF-8 BOM would
    # become the first characters of the secret.
    $oldInput = [Console]::InputEncoding
    try { [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    try { $p = [System.Diagnostics.Process]::Start($psi) }
    finally { try { [Console]::InputEncoding = $oldInput } catch { } }
    $bytes = [Text.Encoding]::UTF8.GetBytes($InputText + "`n")
    $p.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
    $p.StandardInput.Close()
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    if ($stdout) { Write-Host $stdout.TrimEnd() }
    if ($stderr) { Write-Host $stderr.TrimEnd() -ForegroundColor Yellow }
    if ($p.ExitCode -ne 0) { throw "Remote command failed (exit $($p.ExitCode)): $Command" }
}

function Copy-ToRemote($Target, [string]$LocalPath, [string]$RemotePath) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & scp @(Get-SshOptions $Target) $LocalPath "$($Target.Address):$RemotePath" | Out-Host }
    finally { $ErrorActionPreference = $old }
    if ($LASTEXITCODE -ne 0) { throw "Upload failed: $LocalPath -> $RemotePath" }
}

# A value for a bash script: single-quoted, with ' written as '\''.
function ConvertTo-BashLiteral([string]$Value) {
    return "'" + $Value.Replace("'", "'\''") + "'"
}

# ------------------------------------------------------------------ server package

# <yyyyMMdd-HHmm>-<git commit>[-dirty]: sortable, and tells exactly which code a server runs (/api/health shows it).
function New-ReleaseId {
    $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmm")
    $sha = (Invoke-Native "git" @("-C", $CodeRoot, "rev-parse", "--short", "HEAD")).Out.Trim()
    if (-not $sha) { $sha = "nogit" }
    $dirty = (Invoke-Native "git" @("-C", $CodeRoot, "status", "--porcelain")).Out.Trim()
    if ($dirty) { return "$stamp-$sha-dirty" }
    return "$stamp-$sha"
}

function Assert-CleanWorkingTree {
    $dirty = (Invoke-Native "git" @("-C", $CodeRoot, "status", "--porcelain")).Out.Trim()
    if ($dirty) {
        Write-Host "Uncommitted changes:" -ForegroundColor Yellow
        Write-Host $dirty
        if (-not (Read-YesNo "Build anyway? The release id will end in '-dirty'." $false)) { throw "Cancelled: commit first." }
    }
}

# Builds code\dist\server\nadlan-server-<id>.tar.gz: self-contained linux-x64 binaries of the app and the DB tool
# (the server needs no .NET install and gets no source code) + the server scripts from release\server.
function New-ServerPackage([string]$ReleaseId, [bool]$RunTests = $true) {
    Assert-Tool "dotnet" "Install the .NET 9 SDK."
    $outDir = Join-Path $DistRoot "server"
    $stage = Join-Path $outDir "stage"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null

    if ($RunTests) {
        Write-Step "Running tests"
        & dotnet test (Join-Path $CodeRoot "tests\Nadlan.Core.Tests\Nadlan.Core.Tests.csproj") --nologo -v quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing was built." }
    }

    $projects = [ordered]@{ "app" = "src\Nadlan.Host\Nadlan.Host.csproj"; "dbtool" = "src\Nadlan.DbTool\Nadlan.DbTool.csproj" }
    foreach ($name in $projects.Keys) {
        Write-Step "Publishing $name (linux-x64, self-contained, release $ReleaseId)"
        & dotnet publish (Join-Path $CodeRoot $projects[$name]) -c Release -r linux-x64 --self-contained true `
            -o (Join-Path $stage $name) -p:DebugType=None -p:DebugSymbols=false `
            -p:InformationalVersion=$ReleaseId -p:IncludeSourceRevisionInInformationalVersion=false --nologo -v quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $name" }
    }

    # Production gets binaries only: no dev settings, symbols or source.
    Get-ChildItem $stage -Recurse -File -Include "appsettings.Development.json", "*.pdb", "*.cs", "*.csproj" | Remove-Item -Force
    foreach ($required in @("app\Nadlan.Host", "app\Nadlan.Host.dll", "app\wwwroot\index.html", "dbtool\Nadlan.DbTool")) {
        if (-not (Test-Path (Join-Path $stage $required))) { throw "Publish output is missing $required" }
    }

    # Server scripts and configs with Unix line endings, whatever git did on checkout.
    $serverOut = Join-Path $stage "server"
    New-Item -ItemType Directory -Force $serverOut | Out-Null
    foreach ($file in Get-ChildItem (Join-Path $ReleaseRoot "server") -File) {
        $text = [IO.File]::ReadAllText($file.FullName) -replace "`r`n", "`n"
        [IO.File]::WriteAllText((Join-Path $serverOut $file.Name), $text, (New-Object System.Text.UTF8Encoding($false)))
    }
    [IO.File]::WriteAllText((Join-Path $stage "RELEASE"), "$ReleaseId`n")
    $commit = (Invoke-Native "git" @("-C", $CodeRoot, "rev-parse", "HEAD")).Out.Trim()
    $info = "release=$ReleaseId`ncommit=$commit`nbuilt_utc=$((Get-Date).ToUniversalTime().ToString('o'))`nbuilt_by=$env:USERNAME@$env:COMPUTERNAME`n"
    [IO.File]::WriteAllText((Join-Path $stage "BUILD-INFO"), $info)

    $package = Join-Path $outDir "nadlan-server-$ReleaseId.tar.gz"
    if (Test-Path $package) { Remove-Item $package -Force }
    # Windows' own tar (bsdtar); the server sets the executable bits when it unpacks (deploy.sh).
    & (Join-Path $env:SystemRoot "System32\tar.exe") -czf $package -C $stage . | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }
    Remove-Item $stage -Recurse -Force
    Write-Host ("Package: {0} ({1:N0} MB)" -f $package, ((Get-Item $package).Length / 1MB)) -ForegroundColor Green
    return $package
}

# Uploads a server package to the server's home folder and unpacks it to ~/nadlan-upload (the home folder is private,
# so install.conf stays unreadable to other accounts). Returns the remote folder.
function Send-ServerPackage($Target, [string]$Package) {
    $name = Split-Path -Leaf $Package
    Write-Step "Uploading $name to $($Target.Host)"
    Invoke-Remote $Target "rm -rf ~/nadlan-upload && mkdir -p ~/nadlan-upload"
    Copy-ToRemote $Target $Package "nadlan-upload/$name"
    Invoke-Remote $Target "cd ~/nadlan-upload && tar -xzf $name && rm -f $name"
    return "/home/$($Target.User)/nadlan-upload"
}

# Finds the newest built server package, or "" if none.
function Get-LatestServerPackage {
    $dir = Join-Path $DistRoot "server"
    if (-not (Test-Path $dir)) { return "" }
    $latest = Get-ChildItem $dir -Filter "nadlan-server-*.tar.gz" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) { return $latest.FullName }
    return ""
}

# Asks whether to build a new package or reuse the newest one, and returns the package path.
function Get-ServerPackage([bool]$Build, [bool]$RunTests) {
    $latest = Get-LatestServerPackage
    if (-not $Build -and $latest) {
        # Name: nadlan-server-<time>-<commit>[-dirty].tar.gz. Say plainly when it is older than the code: reusing an old
        # package silently re-runs old server scripts.
        $name = Split-Path -Leaf $latest
        $packageCommit = ([regex]::Match($name, '-([0-9a-f]{7,})(-dirty)?\.tar\.gz$')).Groups[1].Value
        $head = "$((Invoke-Native "git" @("-C", $CodeRoot, "rev-parse", "--short", "HEAD")).Out)".Trim()
        $note = if ($packageCommit -and $head -and -not $head.StartsWith($packageCommit) -and -not $packageCommit.StartsWith($head)) {
            " It was built from commit $packageCommit, OLDER than your code ($head)."
        } else { "" }
        if ($note) { Write-Warning "The newest built package is $name.$note" }
        $Build = -not (Read-YesNo "Deploy the existing package $name?$note (n = build a new one from the current code)" $false)
    }
    if (-not $Build -and $latest) { return $latest }
    Assert-CleanWorkingTree
    return New-ServerPackage (New-ReleaseId) $RunTests
}

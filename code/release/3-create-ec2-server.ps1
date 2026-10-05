<#
.SYNOPSIS
Optional: creates the EC2 server (Amazon Linux 2023, t3.micro by default) with key, firewall, S3 role and fixed IP.

.DESCRIPTION
Skip this if you already have an Amazon Linux 2023 instance. Asks which AWS identity (CLI profile) to use, then:
  - key pair "<name>" (private key saved to %USERPROFILE%\.ssh\<name>.pem, readable only by you)
  - security group "<name>-web": 80/443 from anywhere, 22 (SSH) only from this PC's current public IP
  - optional IAM role "<name>-role" allowed only on s3://<bucket>/<folder>/ (the app's file storage; no access keys)
  - the instance: x86_64, 20 GB encrypted gp3 disk, IMDSv2, T-class CPU credits "standard" (no surprise charges)
  - the disk is kept if the instance is terminated, and (optional, recommended) snapshotted daily, last 7 kept (AWS DLM)
  - optional Elastic IP, so the address (and your DNS record) survives stop/start
Safe to re-run: an existing instance with the same Name tag is reused (e.g. to open SSH for your new IP, or to add the S3 role).
Next: point your domain's DNS A record at the printed IP, then run 4-install-server.ps1.
#>
param(
    [string]$AwsProfile = "",
    [string]$Region = "",
    [string]$Name = "",
    [string]$InstanceType = "",
    [string]$StorageBucket = "",
    [string]$StorageRoot = ""
)
. (Join-Path $PSScriptRoot "lib\common.ps1")

$AwsProfile = Select-AwsProfile $AwsProfile "creating the EC2 server (EC2, IAM role, security group)" "awsProfileEc2"
$Region = Read-Value "AWS region" $Region -Setting "awsRegion" -Default "eu-central-1" -Pattern '^[a-z]{2}(-[a-z]+)+-\d$' -PatternHint "Like eu-central-1"
$Name = Read-Value "Server name (EC2 Name tag)" $Name -Setting "ec2Name" -Default "nadlan-prod" -Pattern '^[A-Za-z0-9-]{3,40}$' -PatternHint "3-40 letters, digits or '-'."
$InstanceType = Read-Value "Instance type" $InstanceType -Setting "ec2InstanceType" -Default "t3.micro" -Pattern '^[a-z0-9]+\.[a-z0-9]+$'
if ($InstanceType -match '^[a-z]+\d+[a-z]*g[a-z]*\.') { throw "$InstanceType is ARM (Graviton); the server package is built for x86_64 (linux-x64). Use e.g. t3.micro or t3.small." }
$StorageBucket = Read-Value "S3 bucket for Nadlan files (gives the server a role for it)" $StorageBucket -Setting "storageBucket" -Optional `
    -Pattern '^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$' -PatternHint "An S3 bucket name."
if ($StorageBucket) {
    $StorageRoot = (Read-Value "Folder in the bucket for this server" $StorageRoot -Setting "storageRoot" -Default "nadlan/prod" -Pattern '^[A-Za-z0-9._/-]*$').Trim('/')
}

function Ec2([string[]]$Arguments, [switch]$AllowFailure) {
    return Invoke-Aws $AwsProfile (@($Arguments) + @("--region", $Region)) -AllowFailure:$AllowFailure
}
function Out-Json($r) { return ($r.Out | ConvertFrom-Json) }

# Writes a JSON value to a temp file and returns "file://<path>" for the AWS CLI (UTF-8 without BOM).
$tempFiles = @()
function JsonArg($value) {
    $path = Join-Path $env:TEMP ("nadlan-" + [guid]::NewGuid().ToString("N") + ".json")
    $json = if ($value -is [string]) { $value } else { ConvertTo-Json -InputObject $value -Depth 20 }
    [IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
    $script:tempFiles += $path
    return "file://$path"
}

try {
    # ---------------------------------------------------------------- existing instance?
    $found = Out-Json (Ec2 @("ec2", "describe-instances", "--filters", "Name=tag:Name,Values=$Name",
            "Name=instance-state-name,Values=pending,running,stopping,stopped"))
    $existing = @($found.Reservations | ForEach-Object { $_.Instances })
    if ($existing.Count -gt 1) { throw "More than one instance is tagged Name=$Name; rename one or pick another name." }
    $instance = if ($existing.Count -eq 1) { $existing[0] } else { $null }
    if ($instance) { Write-Host "Instance $($instance.InstanceId) ($($instance.State.Name)) already exists - reusing it." -ForegroundColor Yellow }

    # ---------------------------------------------------------------- key pair
    Write-Step "Key pair '$Name'"
    $sshDir = Join-Path $env:USERPROFILE ".ssh"
    New-Item -ItemType Directory -Force $sshDir | Out-Null
    $keyPath = Join-Path $sshDir "$Name.pem"
    $keyExists = (Ec2 @("ec2", "describe-key-pairs", "--key-names", $Name) -AllowFailure).Ok
    if ($instance -and $instance.KeyName -and $instance.KeyName -ne $Name) {
        Write-Host "The instance uses key pair '$($instance.KeyName)'."
        $keyPath = Read-Value "Its private key file (.pem)" -Setting "sshKeyPath"
    }
    elseif ($keyExists) {
        if (-not (Test-Path $keyPath)) { $keyPath = Read-Value "Key pair '$Name' exists in AWS. Its private key file (.pem)" -Setting "sshKeyPath" }
        Write-Host "exists; private key: $keyPath"
    }
    else {
        $created = Out-Json (Ec2 @("ec2", "create-key-pair", "--key-name", $Name, "--key-type", "ed25519", "--key-format", "pem",
                "--tag-specifications", "ResourceType=key-pair,Tags=[{Key=Name,Value=$Name}]"))
        [IO.File]::WriteAllText($keyPath, ($created.KeyMaterial -replace "`r`n", "`n"))
        & icacls $keyPath /inheritance:r /grant:r "$($env:USERNAME):R" | Out-Null
        Write-Host "created; private key saved to $keyPath (only your user can read it - keep a copy somewhere safe)"
    }

    # ---------------------------------------------------------------- security group
    $sgName = "$Name-web"
    Write-Step "Security group '$sgName'"
    $myIp = (Invoke-RestMethod -Uri "https://checkip.amazonaws.com" -TimeoutSec 15).ToString().Trim()
    if ($myIp -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Couldn't find this PC's public IP (got '$myIp')." }
    $sgs = @((Out-Json (Ec2 @("ec2", "describe-security-groups", "--filters", "Name=group-name,Values=$sgName"))).SecurityGroups)
    if ($sgs.Count -gt 0) {
        $sgId = $sgs[0].GroupId
        Write-Host "exists: $sgId"
    }
    else {
        $vpcs = @((Out-Json (Ec2 @("ec2", "describe-vpcs", "--filters", "Name=is-default,Values=true"))).Vpcs)
        if ($vpcs.Count -eq 0) { throw "No default VPC in $Region. Create the instance in the console, then use 4-install-server.ps1." }
        $sgId = (Out-Json (Ec2 @("ec2", "create-security-group", "--group-name", $sgName, "--description", "Nadlan web server",
                    "--vpc-id", $vpcs[0].VpcId))).GroupId
        $web = @(80, 443 | ForEach-Object { @{ IpProtocol = "tcp"; FromPort = $_; ToPort = $_; IpRanges = @(@{ CidrIp = "0.0.0.0/0" }); Ipv6Ranges = @(@{ CidrIpv6 = "::/0" }) } })
        Ec2 @("ec2", "authorize-security-group-ingress", "--group-id", $sgId, "--ip-permissions", (JsonArg $web)) | Out-Null
        Write-Host "created: $sgId (80, 443 open)"
    }
    $ssh = @(@{ IpProtocol = "tcp"; FromPort = 22; ToPort = 22; IpRanges = @(@{ CidrIp = "$myIp/32"; Description = "SSH from $env:COMPUTERNAME" }) })
    $r = Ec2 @("ec2", "authorize-security-group-ingress", "--group-id", $sgId, "--ip-permissions", (JsonArg $ssh)) -AllowFailure
    if ($r.Ok) { Write-Host "SSH (22) allowed from $myIp" }
    elseif ($r.Err -match "Duplicate") { Write-Host "SSH (22) already allowed from $myIp" }
    else { throw "Couldn't open SSH: $($r.Err)" }

    # ---------------------------------------------------------------- IAM role for S3
    $profileName = ""
    if ($StorageBucket) {
        $roleName = "$Name-role"
        $profileName = "$Name-profile"
        Write-Step "IAM role '$roleName' (S3: $StorageBucket/$StorageRoot/)"
        if (-not (Invoke-Aws $AwsProfile @("iam", "get-role", "--role-name", $roleName) -AllowFailure).Ok) {
            $trust = @{ Version = "2012-10-17"; Statement = @(@{ Effect = "Allow"; Principal = @{ Service = "ec2.amazonaws.com" }; Action = "sts:AssumeRole" }) }
            Invoke-Aws $AwsProfile @("iam", "create-role", "--role-name", $roleName, "--assume-role-policy-document", (JsonArg $trust),
                "--description", "Nadlan server $Name - S3 file storage") | Out-Null
            Write-Host "role created"
        }
        $rootPrefix = if ($StorageRoot) { "$StorageRoot/" } else { "" }
        $policy = (Get-Content (Join-Path $CodeRoot "aws\iam-policy-app.json") -Raw).Replace("__BUCKET__", $StorageBucket).Replace("__ROOT_PREFIX__", $rootPrefix)
        Invoke-Aws $AwsProfile @("iam", "put-role-policy", "--role-name", $roleName, "--policy-name", "NadlanFileStorage", "--policy-document", (JsonArg $policy)) | Out-Null
        Write-Host "policy NadlanFileStorage: objects under s3://$StorageBucket/$rootPrefix only"
        if (-not (Invoke-Aws $AwsProfile @("iam", "get-instance-profile", "--instance-profile-name", $profileName) -AllowFailure).Ok) {
            Invoke-Aws $AwsProfile @("iam", "create-instance-profile", "--instance-profile-name", $profileName) | Out-Null
            Invoke-Aws $AwsProfile @("iam", "add-role-to-instance-profile", "--instance-profile-name", $profileName, "--role-name", $roleName) | Out-Null
            Write-Host "instance profile created (waiting 15 s for IAM to propagate)"
            Start-Sleep -Seconds 15
        }
    }

    # ---------------------------------------------------------------- instance
    if (-not $instance) {
        Write-Step "Launching $InstanceType '$Name'"
        $ami = (Out-Json (Ec2 @("ssm", "get-parameter", "--name", "/aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-x86_64"))).Parameter.Value
        Write-Host "Amazon Linux 2023 image: $ami"
        $disk = @(@{ DeviceName = "/dev/xvda"; Ebs = @{ VolumeSize = 20; VolumeType = "gp3"; Encrypted = $true; DeleteOnTermination = $false } })
        $launch = @("ec2", "run-instances", "--image-id", $ami, "--instance-type", $InstanceType, "--key-name", $Name,
            "--security-group-ids", $sgId, "--block-device-mappings", (JsonArg $disk),
            "--metadata-options", "HttpTokens=required,HttpEndpoint=enabled",
            "--tag-specifications", "ResourceType=instance,Tags=[{Key=Name,Value=$Name}]", "ResourceType=volume,Tags=[{Key=Name,Value=$Name}]")
        if ($InstanceType -like "t*") { $launch += @("--credit-specification", "CpuCredits=standard") }
        if ($profileName) { $launch += @("--iam-instance-profile", "Name=$profileName") }
        for ($attempt = 1; ; $attempt++) {
            $r = Ec2 $launch -AllowFailure
            if ($r.Ok) { break }
            # A brand-new instance profile can take a little while to become usable.
            if ($attempt -lt 6 -and $r.Err -match "(?i)instance profile") { Write-Host "IAM not ready yet, retrying in 10 s..."; Start-Sleep -Seconds 10; continue }
            throw "run-instances failed: $($r.Err)"
        }
        $instance = (Out-Json $r).Instances[0]
        Write-Host "launched $($instance.InstanceId)"
    }
    elseif ($profileName -and -not $instance.IamInstanceProfile) {
        Ec2 @("ec2", "associate-iam-instance-profile", "--instance-id", $instance.InstanceId, "--iam-instance-profile", "Name=$profileName") | Out-Null
        Write-Host "S3 role attached to $($instance.InstanceId)"
    }
    $instanceId = $instance.InstanceId
    if ($instance.State.Name -in @("stopped", "stopping")) {
        if (Read-YesNo "The instance is $($instance.State.Name). Start it?" $true) {
            Ec2 @("ec2", "wait", "instance-stopped", "--instance-ids", $instanceId) | Out-Null
            Ec2 @("ec2", "start-instances", "--instance-ids", $instanceId) | Out-Null
        }
    }
    Write-Host "Waiting for $instanceId to run..."
    Ec2 @("ec2", "wait", "instance-running", "--instance-ids", $instanceId) | Out-Null

    # ---------------------------------------------------------------- disk protection
    # The disk holds the database: terminating the instance (by mistake) must not delete it, and a daily snapshot
    # (kept 7 days) survives losing the disk itself. backup.sh also copies each nightly dump to S3.
    Write-Step "Disk protection"
    $described = (Out-Json (Ec2 @("ec2", "describe-instances", "--instance-ids", $instanceId))).Reservations[0].Instances[0]
    $rootMapping = @($described.BlockDeviceMappings | Where-Object { $_.DeviceName -eq $described.RootDeviceName })[0]
    if ($rootMapping.Ebs.DeleteOnTermination) {
        $keep = @(@{ DeviceName = $rootMapping.DeviceName; Ebs = @{ DeleteOnTermination = $false } })
        Ec2 @("ec2", "modify-instance-attribute", "--instance-id", $instanceId, "--block-device-mappings", (JsonArg $keep)) | Out-Null
    }
    Write-Host "disk $($rootMapping.Ebs.VolumeId) is kept if the instance is terminated"
    # Snapshots pick volumes by tag; an instance from before this script tagged its volume may have none.
    Ec2 @("ec2", "create-tags", "--resources", $rootMapping.Ebs.VolumeId, "--tags", "Key=Name,Value=$Name", "Key=NadlanBackup,Value=$Name") | Out-Null

    $listed = Ec2 @("dlm", "get-lifecycle-policies", "--target-tags", "NadlanBackup=$Name")
    $policies = @((Out-Json $listed).Policies | Where-Object { $_ })
    if ($policies.Count -gt 0) {
        Write-Host "daily snapshots: policy $($policies[0].PolicyId) ($($policies[0].State))"
    }
    elseif (Read-YesNo "Take a daily snapshot of the disk, keeping the last 7 (a few cents a month)?" $true) {
        $roleArn = (Out-Json (Invoke-Aws $AwsProfile @("dlm", "create-default-role", "--resource-type", "snapshot", "--region", $Region))).RoleArn
        if (-not $roleArn) {
            $account = (Out-Json (Invoke-Aws $AwsProfile @("sts", "get-caller-identity"))).Account
            $roleArn = "arn:aws:iam::${account}:role/AWSDataLifecycleManagerDefaultRole"
        }
        $details = @{
            PolicyType    = "EBS_SNAPSHOT_MANAGEMENT"
            ResourceTypes = @("VOLUME")
            TargetTags    = @(@{ Key = "NadlanBackup"; Value = $Name })
            Schedules     = @(@{
                    Name       = "daily-7"
                    CreateRule = @{ Interval = 24; IntervalUnit = "HOURS"; Times = @("03:00") } # after backup.sh's 02:30 dump
                    RetainRule = @{ Count = 7 }
                    CopyTags   = $true
                })
        }
        for ($attempt = 1; ; $attempt++) {
            $r = Ec2 @("dlm", "create-lifecycle-policy", "--description", "Nadlan $Name daily disk snapshots", "--state", "ENABLED",
                "--execution-role-arn", $roleArn, "--policy-details", (JsonArg $details)) -AllowFailure
            if ($r.Ok) { break }
            # A just-created default role can take a little while to become usable.
            if ($attempt -lt 6 -and $r.Err -match "(?i)role") { Write-Host "IAM not ready yet, retrying in 10 s..."; Start-Sleep -Seconds 10; continue }
            throw "Couldn't create the snapshot policy: $($r.Err)"
        }
        Write-Host "daily snapshots at 03:00 UTC, last 7 kept: policy $((Out-Json $r).PolicyId)"
    }
    else {
        Write-Warning "No disk snapshots: only the nightly dumps (on the disk and in S3) protect the data."
    }

    # ---------------------------------------------------------------- fixed IP
    $addresses = @((Out-Json (Ec2 @("ec2", "describe-addresses", "--filters", "Name=instance-id,Values=$instanceId"))).Addresses)
    if ($addresses.Count -gt 0) {
        Write-Host "Elastic IP: $($addresses[0].PublicIp)"
    }
    elseif (Read-YesNo "Give it a fixed public IP (Elastic IP)? Recommended: the normal public IP changes on every stop/start." $true) {
        $eip = Out-Json (Ec2 @("ec2", "allocate-address", "--domain", "vpc", "--tag-specifications", "ResourceType=elastic-ip,Tags=[{Key=Name,Value=$Name}]"))
        Ec2 @("ec2", "associate-address", "--instance-id", $instanceId, "--allocation-id", $eip.AllocationId) | Out-Null
        Write-Host "Elastic IP $($eip.PublicIp) attached"
    }
    $ip = (Out-Json (Ec2 @("ec2", "describe-instances", "--instance-ids", $instanceId))).Reservations[0].Instances[0].PublicIpAddress
    if (-not $ip) { throw "The instance has no public IP (is its subnet public?)." }

    Set-Setting "sshHost" $ip
    Set-Setting "sshUser" "ec2-user"
    Set-Setting "sshKeyPath" $keyPath
    Set-Setting "ec2InstanceId" $instanceId

    Write-Step "Waiting for SSH on $ip"
    $target = [pscustomobject]@{ Host = $ip; User = "ec2-user"; Key = $keyPath; Address = "ec2-user@$ip" }
    $ready = $false
    for ($i = 0; $i -lt 30 -and -not $ready; $i++) {
        $ready = (Invoke-Native "ssh" (@(Get-SshOptions $target) + @("-o", "BatchMode=yes", $target.Address, "true"))).Ok
        if (-not $ready) { Start-Sleep -Seconds 5 }
    }
    if ($ready) { Write-Host "SSH ok" -ForegroundColor Green } else { Write-Warning "SSH isn't answering yet; give it a minute." }

    Write-Host ""
    Write-Host "Server '$Name' ($InstanceType, $instanceId) is at $ip" -ForegroundColor Green
    Write-Host "Next:"
    Write-Host "  1. DNS: add an A record for your domain (e.g. nadlan.example.com) -> $ip"
    Write-Host "  2. Run 4-install-server.ps1 (server address $ip, user ec2-user, key $keyPath)"
    if ($StorageBucket) { Write-Host "  The app reaches s3://$StorageBucket/$StorageRoot/ through the instance role; 4-install-server.ps1 sets the bucket's CORS." }
}
finally {
    $tempFiles | ForEach-Object { Remove-Item $_ -ErrorAction SilentlyContinue }
}

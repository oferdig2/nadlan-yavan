# One-time AWS setup for Nadlan file storage. Run it yourself with an ADMIN profile. Works with a NEW bucket or an
# EXISTING shared bucket (files live under -RootFolder; nothing else in the bucket is touched):
#   1. bucket: created (all public access blocked) only if it doesn't exist; an existing bucket's settings are left alone
#   2. CORS: adds/updates only this root folder's rule "nadlan-<root>" (uploads + ETag header); origins accumulate
#   3. lifecycle: adds/updates only "nadlan-abort-uploads-<root>", scoped to RootFolder; other rules are kept
#   4. IAM user "nadlan-<root>" (nadlan/dev → nadlan-dev), allowed ONLY on objects under s3://Bucket/RootFolder/
#   5. its access key, in the local AWS profile of the same name (~/.aws/credentials) - never in the DB or the repo;
#      an existing key is reused only if AWS confirms it belongs to that user in this account
# Every name is per root folder, so dev (nadlan/dev) and prod (nadlan/prod) in one bucket/account never overwrite each
# other. Safe to re-run. S3 "folders" are just key prefixes: nothing needs creating for RootFolder.
#
# Example (shared bucket):
#   .\aws\setup-s3.ps1 -Bucket my-company-bucket -RootFolder nadlan/dev -Region eu-central-1 -AdminProfile futuristic-admin
param(
    [Parameter(Mandatory = $true)][string]$Bucket,
    [Parameter(Mandatory = $true)][AllowEmptyString()][string]$RootFolder,   # e.g. nadlan/dev; "" = bucket root
    [string]$Region = "eu-central-1",
    [Parameter(Mandatory = $true)][string]$AdminProfile,
    [string]$UserName = "",                                                   # default: nadlan-<root>
    [string]$AppProfile = "",                                                 # default: same as the IAM user name
    [string[]]$AllowedOrigins = @("http://localhost:5515")                   # ADDED to the origins already allowed
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $PSCommandPath
$root = $RootFolder.Trim().Trim('/')
$rootPrefix = if ($root) { "$root/" } else { "" }

# The root folder ends up in an IAM ARN: only plain characters, so nothing can turn into a wildcard ('?', '*').
if ($root -notmatch '^[A-Za-z0-9._/-]*$' -or $root -match '//') {
    throw "RootFolder '$RootFolder' may only contain letters, digits, '.', '_', '-' and single '/' separators."
}

# "nadlan/dev" → "dev" (the app name is already in every resource name), "client/files" → "client-files".
$slug = if ($root) { ((($root -replace "^nadlan(/|$)", "") -replace "[^A-Za-z0-9]+", "-").Trim("-")).ToLowerInvariant() } else { "" }
if (-not $slug) { $slug = "root" }
if (-not $UserName) { $UserName = "nadlan-$slug" }
if (-not $AppProfile) { $AppProfile = $UserName }
$corsRuleId = "nadlan-$slug"
$lifecycleRuleId = "nadlan-abort-uploads-$slug"
$policyName = "NadlanFileStorage-$slug"

# Windows PowerShell 5.1 turns a native command's stderr into a terminating error under "Stop", and AWS reports
# "not found" on stderr. So AWS calls run with "Continue" and are judged by exit code / error code only.
function Invoke-AwsRaw {
    $ErrorActionPreference = "Continue"
    $output = & aws @args --profile $AdminProfile --region $Region --output json 2>&1
    return [pscustomobject]@{
        Ok     = ($LASTEXITCODE -eq 0)
        Stdout = (($output | Where-Object { $_ -is [string] }) -join "`n")
        Text   = (($output | ForEach-Object { "$_".Trim() }) | Where-Object { $_ -and $_ -ne "System.Management.Automation.RemoteException" }) -join " "
    }
}

function Invoke-Aws {
    $r = Invoke-AwsRaw @args
    if (-not $r.Ok) { throw "aws $($args -join ' ') failed: $($r.Text)" }
    return $r.Stdout
}

function Test-Aws {
    return (Invoke-AwsRaw @args).Ok
}

# Reads an optional bucket configuration. Returns $null ONLY when AWS says it doesn't exist (missingCode);
# any other failure (AccessDenied, throttling, network) stops the script - otherwise the following "put" would
# replace a shared bucket's whole configuration with ours.
function Get-OptionalConfig([string]$missingCode, [string[]]$awsArgs) {
    $r = Invoke-AwsRaw @awsArgs
    if ($r.Ok) { return ($r.Stdout | ConvertFrom-Json) }
    if ($r.Text -match $missingCode) { return $null }
    throw "aws $($awsArgs -join ' ') failed: $($r.Text)"
}

# Writes JSON to a temp file (UTF-8, no BOM - the AWS CLI reads file:// as UTF-8), runs the action, always cleans up.
function Invoke-WithJsonFile($object, [scriptblock]$action) {
    $path = Join-Path $env:TEMP ("nadlan-" + [guid]::NewGuid().ToString("N") + ".json")
    $json = if ($object -is [string]) { $object } else { $object | ConvertTo-Json -Depth 20 }
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
    try { & $action "file://$path" } finally { Remove-Item $path -ErrorAction SilentlyContinue }
}

# --- 1. Bucket
Write-Host "== Bucket $Bucket ($Region), root folder '$rootPrefix'" -ForegroundColor Cyan
if (Test-Aws s3api head-bucket --bucket $Bucket) {
    Write-Host "   exists - its settings are not changed"
    # The app must sign URLs for the bucket's REAL region; the CLI follows redirects, so -Region alone would go unnoticed.
    $location = (Invoke-Aws s3api get-bucket-location --bucket $Bucket | ConvertFrom-Json).LocationConstraint
    $actualRegion = if ($location) { $location } else { "us-east-1" }
    if ($actualRegion -ne $Region) {
        Write-Host "   bucket is in $actualRegion (not $Region) - using $actualRegion" -ForegroundColor Yellow
        $Region = $actualRegion
    }
    $pab = Get-OptionalConfig "NoSuchPublicAccessBlockConfiguration" @("s3api", "get-public-access-block", "--bucket", $Bucket)
    $c = if ($pab) { $pab.PublicAccessBlockConfiguration } else { $null }
    if (-not ($c -and $c.BlockPublicAcls -and $c.IgnorePublicAcls -and $c.BlockPublicPolicy -and $c.RestrictPublicBuckets)) {
        Write-Host "   WARNING: public access is not fully blocked on this bucket. Nadlan files are private by design." -ForegroundColor Yellow
    }
} else {
    if ($Region -eq "us-east-1") {
        Invoke-Aws s3api create-bucket --bucket $Bucket | Out-Null
    } else {
        Invoke-Aws s3api create-bucket --bucket $Bucket --create-bucket-configuration "LocationConstraint=$Region" | Out-Null
    }
    Invoke-Aws s3api put-public-access-block --bucket $Bucket `
        --public-access-block-configuration "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true" | Out-Null
    Write-Host "   created, public access blocked"
}

# --- 2. CORS: our rule merged into the bucket's rules. Origins accumulate (re-running with a new origin adds it).
$ourCors = (Get-Content (Join-Path $here "s3-cors.json") -Raw | ConvertFrom-Json).CORSRules[0]
$ourCors.ID = $corsRuleId
$existing = Get-OptionalConfig "NoSuchCORSConfiguration" @("s3api", "get-bucket-cors", "--bucket", $Bucket)
$existingRules = @(if ($existing) { $existing.CORSRules })
$previousOrigins = @($existingRules | Where-Object { $_.ID -eq $corsRuleId } | ForEach-Object { $_.AllowedOrigins })
$otherCors = @($existingRules | Where-Object { $_.ID -ne $corsRuleId })
$ourCors.AllowedOrigins = @(@($previousOrigins) + @($ourCors.AllowedOrigins) + @($AllowedOrigins) | Where-Object { $_ } | Select-Object -Unique)
# Ours FIRST: S3 answers with the first rule that matches, and an earlier broad rule without ExposeHeaders ETag
# would otherwise hide the ETag header and break every upload.
Invoke-WithJsonFile @{ CORSRules = @(@($ourCors) + $otherCors) } {
    param($file) Invoke-Aws s3api put-bucket-cors --bucket $Bucket --cors-configuration $file | Out-Null
}
Write-Host "   CORS rule '$corsRuleId' allows $($ourCors.AllowedOrigins -join ', ') ($($otherCors.Count) other rule(s) kept)"

# --- 3. Lifecycle: our rule (scoped to this root folder) merged into the bucket's rules
$ourRule = ((Get-Content (Join-Path $here "s3-lifecycle.json") -Raw).Replace("__ROOT_PREFIX__", $rootPrefix) | ConvertFrom-Json).Rules[0]
$ourRule.ID = $lifecycleRuleId
$existing = Get-OptionalConfig "NoSuchLifecycleConfiguration" @("s3api", "get-bucket-lifecycle-configuration", "--bucket", $Bucket)
# @(...) around the whole if: PowerShell unrolls a one-item array returned from an if block into a bare object.
$otherRules = @(if ($existing) { $existing.Rules | Where-Object { $_.ID -ne $lifecycleRuleId } })
# A bucket-level lifecycle setting lives outside .Rules; pass it back unchanged or the PUT would reset it.
$transitionSize = if ($existing -and $existing.TransitionDefaultMinimumObjectSize) { $existing.TransitionDefaultMinimumObjectSize } else { $null }
Invoke-WithJsonFile @{ Rules = @($otherRules + $ourRule) } {
    param($file)
    $putArgs = @("s3api", "put-bucket-lifecycle-configuration", "--bucket", $Bucket, "--lifecycle-configuration", $file)
    if ($transitionSize) { $putArgs += @("--transition-default-minimum-object-size", $transitionSize) }
    Invoke-Aws @putArgs | Out-Null
}
Write-Host "   lifecycle rule '$lifecycleRuleId': abort unfinished uploads under '$rootPrefix' after 1 day ($($otherRules.Count) other rule(s) kept)"

# --- 4. IAM user limited to the root folder
Write-Host "== IAM user $UserName" -ForegroundColor Cyan
if (Test-Aws iam get-user --user-name $UserName) {
    Write-Host "   exists - keeping it"
} else {
    Invoke-Aws iam create-user --user-name $UserName | Out-Null
    Write-Host "   created"
}

$policy = (Get-Content (Join-Path $here "iam-policy-app.json") -Raw).Replace("__BUCKET__", $Bucket).Replace("__ROOT_PREFIX__", $rootPrefix)
Invoke-WithJsonFile $policy {
    param($file) Invoke-Aws iam put-user-policy --user-name $UserName --policy-name $policyName --policy-document $file | Out-Null
}
Write-Host "   policy ${policyName}: objects under s3://$Bucket/$rootPrefix only"

# --- 5. Access key into the local profile (dev only; production uses an IAM role instead)
Write-Host "== Local profile '$AppProfile'" -ForegroundColor Cyan
$ErrorActionPreference = "Continue"   # "not set" is reported on stderr
$existingKey = & aws configure get aws_access_key_id --profile $AppProfile 2>$null
$ErrorActionPreference = "Stop"
if ($existingKey) {
    # Reuse the key only if AWS confirms it is THIS user in THIS account; a profile left from another account
    # (e.g. before moving to the client's) or another root folder would silently give AccessDenied later.
    $admin = Invoke-Aws sts get-caller-identity | ConvertFrom-Json
    $expectedArn = "arn:aws:iam::$($admin.Account):user/$UserName"
    $ErrorActionPreference = "Continue"
    $identityJson = & aws sts get-caller-identity --profile $AppProfile --output json 2>$null
    $ErrorActionPreference = "Stop"
    $actualArn = if ($LASTEXITCODE -eq 0) { ($identityJson -join "`n" | ConvertFrom-Json).Arn } else { "(key rejected by AWS)" }
    if ($actualArn -ne $expectedArn) {
        throw "Local profile '$AppProfile' has a key for $actualArn, not $expectedArn. Use another -AppProfile, or remove that profile's key from ~/.aws/credentials and re-run."
    }

    Write-Host "   already has a valid key for $UserName - not creating another"
} else {
    $key = Invoke-Aws iam create-access-key --user-name $UserName | ConvertFrom-Json
    & aws configure set aws_access_key_id $key.AccessKey.AccessKeyId --profile $AppProfile
    & aws configure set aws_secret_access_key $key.AccessKey.SecretAccessKey --profile $AppProfile
    & aws configure set region $Region --profile $AppProfile
    Write-Host "   access key created and stored in ~/.aws/credentials [$AppProfile]"
}

Write-Host ""
Write-Host "Done. Point the app at it (from the code folder), then restart .\start-nadlan.ps1:" -ForegroundColor Green
Write-Host "  .\config.ps1 set ms:host Nadlan:Storage:Bucket $Bucket"
Write-Host "  .\config.ps1 set ms:host Nadlan:Storage:RootFolder $(if ($root) { $root } else { '--empty' })"
Write-Host "  .\config.ps1 set ms:host Nadlan:Storage:Region $Region"
Write-Host "  .\config.ps1 set ms:host Nadlan:Storage:AwsProfile $AppProfile"

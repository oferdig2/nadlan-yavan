# One-time AWS setup for Nadlan file storage (dev). Run it yourself with an ADMIN profile; it creates:
#   1. a private S3 bucket (all public access blocked)
#   2. CORS so the browser can upload parts directly and read the ETag header
#   3. a lifecycle rule that aborts unfinished multipart uploads after 1 day (no orphaned storage costs)
#   4. an IAM user "nadlan-dev" limited to objects in that bucket (iam-policy-app.json)
#   5. an access key for it, stored ONLY in your local AWS profile "nadlan" (~/.aws/credentials) - never in the DB or repo
# Safe to re-run: existing bucket/user are kept; a new key is created only if the "nadlan" profile has none.
#
# Example:  .\aws\setup-s3.ps1 -Bucket nadlan-files-dev -Region eu-central-1 -AdminProfile futuristic-admin
param(
    [Parameter(Mandatory = $true)][string]$Bucket,
    [string]$Region = "eu-central-1",
    [Parameter(Mandatory = $true)][string]$AdminProfile,
    [string]$UserName = "nadlan-dev",
    [string]$AppProfile = "nadlan"
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $PSCommandPath

function Invoke-Aws {
    # Runs aws with the admin profile; throws with AWS's message on failure.
    $output = & aws @args --profile $AdminProfile --region $Region 2>&1
    if ($LASTEXITCODE -ne 0) { throw "aws $($args -join ' ') failed: $output" }
    return $output
}

function Test-Aws {
    & aws @args --profile $AdminProfile --region $Region *> $null
    return $LASTEXITCODE -eq 0
}

Write-Host "== Bucket $Bucket ($Region)" -ForegroundColor Cyan
if (Test-Aws s3api head-bucket --bucket $Bucket) {
    Write-Host "   exists - keeping it"
} else {
    if ($Region -eq "us-east-1") {
        Invoke-Aws s3api create-bucket --bucket $Bucket | Out-Null
    } else {
        Invoke-Aws s3api create-bucket --bucket $Bucket --create-bucket-configuration "LocationConstraint=$Region" | Out-Null
    }
    Write-Host "   created"
}

Invoke-Aws s3api put-public-access-block --bucket $Bucket `
    --public-access-block-configuration "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true" | Out-Null
Write-Host "   public access blocked"
Invoke-Aws s3api put-bucket-cors --bucket $Bucket --cors-configuration "file://$here/s3-cors.json" | Out-Null
Write-Host "   CORS applied (origin http://localhost:5515, exposes ETag)"
Invoke-Aws s3api put-bucket-lifecycle-configuration --bucket $Bucket --lifecycle-configuration "file://$here/s3-lifecycle.json" | Out-Null
Write-Host "   lifecycle: abort unfinished uploads after 1 day"

Write-Host "== IAM user $UserName" -ForegroundColor Cyan
if (Test-Aws iam get-user --user-name $UserName) {
    Write-Host "   exists - keeping it"
} else {
    Invoke-Aws iam create-user --user-name $UserName | Out-Null
    Write-Host "   created"
}

$policyFile = Join-Path $env:TEMP "nadlan-iam-policy.json"
(Get-Content (Join-Path $here "iam-policy-app.json") -Raw).Replace("__BUCKET__", $Bucket) | Set-Content -Path $policyFile -Encoding ascii
Invoke-Aws iam put-user-policy --user-name $UserName --policy-name NadlanFileStorage --policy-document "file://$policyFile" | Out-Null
Remove-Item $policyFile
Write-Host "   policy NadlanFileStorage attached (objects in $Bucket only)"

Write-Host "== Local profile '$AppProfile'" -ForegroundColor Cyan
$existingKey = & aws configure get aws_access_key_id --profile $AppProfile 2>$null
if ($existingKey) {
    Write-Host "   already has a key - not creating another"
} else {
    $key = Invoke-Aws iam create-access-key --user-name $UserName | ConvertFrom-Json
    & aws configure set aws_access_key_id $key.AccessKey.AccessKeyId --profile $AppProfile
    & aws configure set aws_secret_access_key $key.AccessKey.SecretAccessKey --profile $AppProfile
    & aws configure set region $Region --profile $AppProfile
    Write-Host "   access key created and stored in ~/.aws/credentials [$AppProfile]"
}

Write-Host ""
Write-Host "Done. Now point the app at the bucket (from the code folder):" -ForegroundColor Green
Write-Host "  .\config.ps1 set ms:host Nadlan:Storage:Bucket $Bucket"
Write-Host "  .\config.ps1 set ms:host Nadlan:Storage:Region $Region"
Write-Host "  .\config.ps1 set ms:host Nadlan:Storage:AwsProfile $AppProfile"
Write-Host "and restart .\start-nadlan.ps1"

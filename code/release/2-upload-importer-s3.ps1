<#
.SYNOPSIS
Uploads a built importer version to S3 (private) and prints download links for admins.

.DESCRIPTION
Asks which AWS identity (CLI profile) to use - it may differ from the one that runs the server - then uploads
code\dist\NadlanKaekImporter-<version>-* (packages, .sha256, README) to
  s3://<bucket>/<prefix>/<version>/
and writes s3://<bucket>/<prefix>/latest.json. The objects stay private: the script prints pre-signed links
(valid up to 7 days) to send to admins, and saves them in code\dist\NadlanKaekImporter-<version>-links.txt.
Run it again later just to get fresh links (it skips files that are already uploaded unchanged).
#>
param(
    [string]$Version = "",
    [string]$AwsProfile = "",
    [string]$Bucket = "",
    [string]$Prefix = "",
    [int]$LinkDays = 0
)
. (Join-Path $PSScriptRoot "lib\common.ps1")

# Versions that have been built, newest first.
$built = @(Get-ChildItem $DistRoot -File -Filter "NadlanKaekImporter-*-README.txt" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | ForEach-Object { $_.Name -replace '^NadlanKaekImporter-(.+)-README\.txt$', '$1' })
if ($built.Count -eq 0) { throw "No importer build in $DistRoot. Run 1-build-importer.ps1 first." }
Write-Host "Built importer versions: $($built -join ', ')"
$Version = Read-Value "Version to upload" $Version -Default $built[0] -Pattern '^\d+\.\d+\.\d+$' -PatternHint "One of: $($built -join ', ')"
$files = @(Get-ChildItem $DistRoot -File | Where-Object { $_.Name -like "NadlanKaekImporter-$Version-*" -and $_.Name -notlike "*-links.txt" } | Sort-Object Name)
$packages = @($files | Where-Object { $_.Name -match '\.(zip|tar\.gz)$' })
if ($packages.Count -eq 0) { throw "No packages for version $Version in $DistRoot." }
foreach ($p in $packages) {
    if (-not (Test-Path "$($p.FullName).sha256")) { throw "Missing $($p.Name).sha256 - rebuild with 1-build-importer.ps1." }
}

$AwsProfile = Select-AwsProfile $AwsProfile "uploading the importer to S3" "awsProfileUpload"
$Bucket = Read-Value "S3 bucket" $Bucket -Setting "importerBucket" -Default (Get-Setting "storageBucket") `
    -Pattern '^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$' -PatternHint "An S3 bucket name (lowercase letters, digits, '.', '-')."
$Prefix = (Read-Value "Folder in the bucket" $Prefix -Setting "importerPrefix" -Default "nadlan/downloads/kaek-importer" `
    -Pattern '^[A-Za-z0-9._/-]+$' -PatternHint "Letters, digits, '.', '_', '-' and '/'.").Trim('/')
if ($LinkDays -le 0) {
    $LinkDays = [int](Read-Value "Download links valid for how many days (1-7)" -Default "7" -Pattern '^[1-7]$' -PatternHint "1 to 7 (S3's limit).")
}

Write-Step "Checking s3://$Bucket"
$location = Invoke-Aws $AwsProfile @("s3api", "get-bucket-location", "--bucket", $Bucket) -AllowFailure
if (-not $location.Ok) { throw "Can't use bucket '$Bucket' with profile '$AwsProfile': $($location.Err)" }
$region = ($location.Out | ConvertFrom-Json).LocationConstraint
if (-not $region) { $region = "us-east-1" }
Write-Host "Region: $region"

$base = "s3://$Bucket/$Prefix/$Version"
Write-Step "Uploading $($files.Count) files to $base/"
$manifest = @()
foreach ($f in $files) {
    $key = "$Prefix/$Version/$($f.Name)"
    $local = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    # Skip a file already uploaded unchanged (sha256 kept in its metadata).
    $head = Invoke-Aws $AwsProfile @("s3api", "head-object", "--bucket", $Bucket, "--key", $key, "--region", $region) -AllowFailure
    $remote = if ($head.Ok) { (($head.Out | ConvertFrom-Json).Metadata).sha256 } else { "" }
    if ($remote -eq $local) {
        Write-Host ("  {0}  unchanged" -f $f.Name) -ForegroundColor DarkGray
    } else {
        Invoke-Aws $AwsProfile @("s3", "cp", $f.FullName, "$base/$($f.Name)", "--region", $region, "--only-show-errors",
            "--metadata", "sha256=$local", "--cache-control", "private, max-age=86400") | Out-Null
        Write-Host ("  {0}  uploaded ({1:N1} MB)" -f $f.Name, ($f.Length / 1MB))
    }
    if ($f.Name -match '\.(zip|tar\.gz)$') {
        $manifest += [ordered]@{ file = $f.Name; key = $key; sizeBytes = $f.Length; sha256 = $local }
    }
}

$latest = [ordered]@{ product = "GreekPlot KAEK Importer"; version = $Version; uploadedUtc = (Get-Date).ToUniversalTime().ToString("o"); packages = $manifest }
$tmp = Join-Path $env:TEMP ("nadlan-latest-" + [guid]::NewGuid().ToString("N") + ".json")
try {
    [IO.File]::WriteAllText($tmp, ($latest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
    Invoke-Aws $AwsProfile @("s3", "cp", $tmp, "s3://$Bucket/$Prefix/latest.json", "--region", $region, "--only-show-errors",
        "--content-type", "application/json", "--cache-control", "no-cache") | Out-Null
    Write-Host "  latest.json -> version $Version"
}
finally { Remove-Item $tmp -ErrorAction SilentlyContinue }

Write-Step "Download links (valid $LinkDays day(s))"
$who = (Invoke-Aws $AwsProfile @("sts", "get-caller-identity")).Out | ConvertFrom-Json
if ($who.Arn -match ':assumed-role/') {
    Write-Warning "This identity uses temporary credentials (SSO / assumed role): the links stop working when its session ends, possibly before $LinkDays day(s). For week-long links use an IAM user's profile."
}
$expires = (Get-Date).AddDays($LinkDays).ToString("yyyy-MM-dd HH:mm")
$lines = @("GreekPlot KAEK Importer $Version - download links (valid until about $expires)", "")
foreach ($f in @($files | Where-Object { $_.Name -match '(\.zip|\.tar\.gz|README\.txt)$' })) {
    $url = (Invoke-Native "aws" @("s3", "presign", "$base/$($f.Name)", "--expires-in", "$($LinkDays * 86400)", "--region", $region, "--profile", $AwsProfile)).Out.Trim()
    if (-not $url) { throw "aws s3 presign failed for $($f.Name)." }
    $lines += $f.Name
    $lines += "  $url"
    $lines += ""
}
$linksPath = Join-Path $DistRoot "NadlanKaekImporter-$Version-links.txt"
[IO.File]::WriteAllText($linksPath, ($lines -join "`r`n"))
$lines | ForEach-Object { Write-Host $_ }
Write-Host "Saved to $linksPath" -ForegroundColor Green
Write-Host "Send admins the README link and the package for their machine. New links any time: run this script again."

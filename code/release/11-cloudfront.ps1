<#
.SYNOPSIS
Sets up the CloudFront CDN for GreekPlot's files (photos, videos, documents): served from an edge near the viewer
(Athens for Greece) instead of the bucket's region. Run again any time: it finds what already exists and reuses it.

.DESCRIPTION
Creates (or reuses, by name) in the chosen AWS account:
  - a signing key pair: the private key is saved on THIS PC only (%USERPROFILE%\.nadlan\cloudfront\<name>-private.pem),
    the public key goes to CloudFront in a key group. Every file URL must be signed with it (the app does that).
  - an Origin Access Control: CloudFront reads the private bucket; the bucket is never public.
  - a cache policy: no cookies/headers, and the one query string response-content-disposition (customer pages open
    files as "photo.jpg", not under the uploader's file name). The signature parameters are not in the cache key.
  - a response headers policy: browsers may keep a file 30 minutes (the app hands out the same URL that long).
  - a CloudFront Function that refuses every path starting with "_" (_backups = database dumps, _downloads, _selftest),
    even with a valid signature.
  - the distribution: origin = s3://<bucket>/<storage root>, price class 100 (Europe incl. Athens + North America).
  - a bucket policy statement letting only this distribution read <storage root>/*.
Then waits until it is deployed, tests it with a signed request (a test file, deleted afterwards), and prints the
server settings. It can apply them on the server over SSH (Mode = CloudFrontSigned); rollback = Mode S3Presigned.
#>
param(
    [string]$AwsProfile = "",
    [string]$Bucket = "",
    [string]$RootFolder = "",
    [string]$Name = "",
    [switch]$SkipWait
)
. (Join-Path $PSScriptRoot "lib\common.ps1")

$AwsProfile = Select-AwsProfile $AwsProfile "the CloudFront CDN for files (CloudFront + the bucket's policy)" "awsProfileCdn"
$Bucket = Read-Value "S3 bucket of the files" $Bucket -Setting "storageBucket" `
    -Pattern '^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$' -PatternHint "An S3 bucket name (lowercase letters, digits, '.', '-')."
$RootFolder = (Read-Value "Storage root folder in the bucket (the server's Nadlan:Storage:RootFolder)" $RootFolder -Setting "storageRoot" `
    -Pattern '^[A-Za-z0-9._/-]+$' -PatternHint "Letters, digits, '.', '_', '-' and '/'.").Trim('/')
if (-not $Name) { $Name = "nadlan-files-" + ($RootFolder -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant() }

$location = Invoke-Aws $AwsProfile @("s3api", "get-bucket-location", "--bucket", $Bucket) -AllowFailure
if (-not $location.Ok) { throw "Can't use bucket '$Bucket' with profile '$AwsProfile': $($location.Err)" }
$region = ($location.Out | ConvertFrom-Json).LocationConstraint
if (-not $region) { $region = "us-east-1" }
$account = ((Invoke-Aws $AwsProfile @("sts", "get-caller-identity")).Out | ConvertFrom-Json).Account
Write-Host "Bucket $Bucket ($region), folder /$RootFolder, account $account, name '$Name'"

$tmpDir = Join-Path $env:TEMP ("nadlan-cdn-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tmpDir | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Write-JsonFile([string]$FileName, $Object) {
    $path = Join-Path $tmpDir $FileName
    [IO.File]::WriteAllText($path, ($Object | ConvertTo-Json -Depth 20 -Compress), $utf8)
    return "file://$path"
}
function Get-CfJson([string[]]$Arguments) { return ((Invoke-Aws $AwsProfile $Arguments).Out | ConvertFrom-Json) }
function New-CallerReference { return "$Name-" + [guid]::NewGuid().ToString("N") }

function Find-OpenSsl {
    $cmd = Get-Command openssl -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @("$env:ProgramFiles\Git\usr\bin\openssl.exe", "$env:ProgramFiles\Git\mingw64\bin\openssl.exe")) { if (Test-Path $p) { return $p } }
    throw "openssl was not found. It comes with Git for Windows (https://git-scm.com)."
}
$openssl = Find-OpenSsl

try {
    # ------------------------------------------------------------ signing key
    Write-Step "Signing key"
    $keyDir = Join-Path $env:USERPROFILE ".nadlan\cloudfront"
    New-Item -ItemType Directory -Force $keyDir | Out-Null
    $pemPath = Join-Path $keyDir "$Name-private.pem"
    $groups = @((Get-CfJson @("cloudfront", "list-key-groups")).KeyGroupList.Items)
    $group = $groups | Where-Object { $_.KeyGroup.KeyGroupConfig.Name -eq $Name } | Select-Object -First 1
    $publicKeyId = ""
    if ($group -and (Test-Path $pemPath)) {
        # The group's key that matches the private key on this PC.
        $localPub = ((Invoke-Native $openssl @("rsa", "-in", $pemPath, "-pubout")).Out -replace "`r", "").Trim()
        foreach ($id in @($group.KeyGroup.KeyGroupConfig.Items)) {
            $pub = (Get-CfJson @("cloudfront", "get-public-key", "--id", $id)).PublicKey.PublicKeyConfig.EncodedKey
            if (($pub -replace "`r", "").Trim() -eq $localPub) { $publicKeyId = $id }
        }
    }
    if (-not $publicKeyId) {
        if (Test-Path $pemPath) { Move-Item $pemPath "$pemPath.old-$(Get-Date -Format yyyyMMddHHmmss)" }
        $gen = Invoke-Native $openssl @("genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048", "-out", $pemPath)
        if (-not $gen.Ok) { throw "openssl could not create the key: $($gen.Err)" }
        $pubPem = ((Invoke-Native $openssl @("rsa", "-in", $pemPath, "-pubout")).Out -replace "`r", "").Trim() + "`n"
        $keyName = "$Name-$(Get-Date -Format yyyyMMddHHmm)"
        $pk = Get-CfJson @("cloudfront", "create-public-key", "--public-key-config",
            (Write-JsonFile "pk.json" @{ CallerReference = (New-CallerReference); Name = $keyName; EncodedKey = $pubPem; Comment = "GreekPlot file URLs ($RootFolder)" }))
        $publicKeyId = $pk.PublicKey.Id
        if ($group) {
            # A key group whose private key isn't on this PC: add the new key next to the old one (both stay valid until
            # the server has the new one; remove the old key afterwards in the CloudFront console).
            $g = Get-CfJson @("cloudfront", "get-key-group", "--id", $group.KeyGroup.Id)
            $cfg = $g.KeyGroup.KeyGroupConfig
            $cfg.Items = @($cfg.Items) + $publicKeyId
            Get-CfJson @("cloudfront", "update-key-group", "--id", $group.KeyGroup.Id, "--if-match", $g.ETag, "--key-group-config", (Write-JsonFile "kg.json" $cfg)) | Out-Null
            $keyGroupId = $group.KeyGroup.Id
            Write-Host "New key $publicKeyId added to the existing key group (the old key still works until removed)." -ForegroundColor Yellow
        } else {
            $kg = Get-CfJson @("cloudfront", "create-key-group", "--key-group-config",
                (Write-JsonFile "kg.json" @{ Name = $Name; Items = @($publicKeyId); Comment = "GreekPlot file URLs ($RootFolder)" }))
            $keyGroupId = $kg.KeyGroup.Id
        }
        Write-Host "Private key saved on this PC only: $pemPath"
    } else {
        $keyGroupId = $group.KeyGroup.Id
        Write-Host "Reusing key $publicKeyId (private key $pemPath)"
    }

    # ------------------------------------------------------------ origin access control
    Write-Step "Origin Access Control"
    $oac = @((Get-CfJson @("cloudfront", "list-origin-access-controls")).OriginAccessControlList.Items) | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    if ($oac) { $oacId = $oac.Id } else {
        $oacId = (Get-CfJson @("cloudfront", "create-origin-access-control", "--origin-access-control-config",
            (Write-JsonFile "oac.json" @{ Name = $Name; Description = "GreekPlot files: CloudFront reads the private bucket"; SigningProtocol = "sigv4"; SigningBehavior = "always"; OriginAccessControlOriginType = "s3" }))).OriginAccessControl.Id
    }
    Write-Host "OAC $oacId"

    # ------------------------------------------------------------ cache + response headers policies
    Write-Step "Cache and response-headers policies"
    $cp = @((Get-CfJson @("cloudfront", "list-cache-policies", "--type", "custom")).CachePolicyList.Items) | Where-Object { $_.CachePolicy.CachePolicyConfig.Name -eq $Name } | Select-Object -First 1
    if ($cp) { $cachePolicyId = $cp.CachePolicy.Id } else {
        $cachePolicyId = (Get-CfJson @("cloudfront", "create-cache-policy", "--cache-policy-config", (Write-JsonFile "cp.json" @{
            Name = $Name; Comment = "GreekPlot files: signed URLs; only response-content-disposition in the key"
            DefaultTTL = 86400; MaxTTL = 31536000; MinTTL = 0
            ParametersInCacheKeyAndForwardedToOrigin = @{
                EnableAcceptEncodingGzip = $false; EnableAcceptEncodingBrotli = $false
                HeadersConfig = @{ HeaderBehavior = "none" }; CookiesConfig = @{ CookieBehavior = "none" }
                QueryStringsConfig = @{ QueryStringBehavior = "whitelist"; QueryStrings = @{ Quantity = 1; Items = @("response-content-disposition") } }
            }
        }))).CachePolicy.Id
    }
    $rp = @((Get-CfJson @("cloudfront", "list-response-headers-policies", "--type", "custom")).ResponseHeadersPolicyList.Items) | Where-Object { $_.ResponseHeadersPolicy.ResponseHeadersPolicyConfig.Name -eq $Name } | Select-Object -First 1
    if ($rp) { $headersPolicyId = $rp.ResponseHeadersPolicy.Id } else {
        $headersPolicyId = (Get-CfJson @("cloudfront", "create-response-headers-policy", "--response-headers-policy-config", (Write-JsonFile "rp.json" @{
            Name = $Name; Comment = "GreekPlot files: browser keeps a file 30 min (the app reuses each signed URL that long)"
            CustomHeadersConfig = @{ Quantity = 1; Items = @(@{ Header = "Cache-Control"; Value = "private, max-age=1800"; Override = $true }) }
        }))).ResponseHeadersPolicy.Id
    }
    Write-Host "cache policy $cachePolicyId, headers policy $headersPolicyId"

    # ------------------------------------------------------------ function: refuse "_" folders
    Write-Step "Function: refuse _backups, _downloads and every other '_' folder"
    $fnName = $Name + "-block-internal"
    $fnCode = Join-Path $tmpDir "fn.js"
    [IO.File]::WriteAllText($fnCode, @'
// GreekPlot files CDN: app files live under <type>/<id>/...; folders starting with "_" (database backups, downloads,
// self-test) are never served here, even with a valid signature.
function handler(event) {
  if (event.request.uri.indexOf('/_') === 0) {
    return { statusCode: 403, statusDescription: 'Forbidden' };
  }
  return event.request;
}
'@, $utf8)
    $fn = Invoke-Aws $AwsProfile @("cloudfront", "describe-function", "--name", $fnName) -AllowFailure
    if ($fn.Ok) {
        $etag = ($fn.Out | ConvertFrom-Json).ETag
        $etag = (Get-CfJson @("cloudfront", "update-function", "--name", $fnName, "--if-match", $etag,
            "--function-config", "Comment=GreekPlot files: refuse _ folders,Runtime=cloudfront-js-2.0", "--function-code", "fileb://$fnCode")).ETag
    } else {
        $etag = (Get-CfJson @("cloudfront", "create-function", "--name", $fnName,
            "--function-config", "Comment=GreekPlot files: refuse _ folders,Runtime=cloudfront-js-2.0", "--function-code", "fileb://$fnCode")).ETag
    }
    Get-CfJson @("cloudfront", "publish-function", "--name", $fnName, "--if-match", $etag) | Out-Null
    $fnArn = (Get-CfJson @("cloudfront", "describe-function", "--name", $fnName, "--stage", "LIVE")).FunctionSummary.FunctionMetadata.FunctionARN
    Write-Host "function $fnArn"

    # ------------------------------------------------------------ distribution
    Write-Step "Distribution"
    $existing = @((Get-CfJson @("cloudfront", "list-distributions")).DistributionList.Items) | Where-Object { $_ -and $_.Comment -eq $Name } | Select-Object -First 1
    $behavior = @{
        TargetOriginId = "s3-files"; ViewerProtocolPolicy = "https-only"; Compress = $false
        AllowedMethods = @{ Quantity = 2; Items = @("GET", "HEAD"); CachedMethods = @{ Quantity = 2; Items = @("GET", "HEAD") } }
        CachePolicyId = $cachePolicyId; ResponseHeadersPolicyId = $headersPolicyId
        TrustedKeyGroups = @{ Enabled = $true; Quantity = 1; Items = @($keyGroupId) }
        FunctionAssociations = @{ Quantity = 1; Items = @(@{ FunctionARN = $fnArn; EventType = "viewer-request" }) }
    }
    $origin = @{
        Id = "s3-files"; DomainName = "$Bucket.s3.$region.amazonaws.com"; OriginPath = "/$RootFolder"
        S3OriginConfig = @{ OriginAccessIdentity = "" }; OriginAccessControlId = $oacId
    }
    if ($existing) {
        $d = Get-CfJson @("cloudfront", "get-distribution-config", "--id", $existing.Id)
        $cfg = $d.DistributionConfig
        $cfg.Origins = @{ Quantity = 1; Items = @($origin) }
        $cfg.DefaultCacheBehavior = $behavior
        $cfg.PriceClass = "PriceClass_100"; $cfg.Enabled = $true
        $dist = (Get-CfJson @("cloudfront", "update-distribution", "--id", $existing.Id, "--if-match", $d.ETag,
            "--distribution-config", (Write-JsonFile "dist.json" $cfg))).Distribution
    } else {
        $dist = (Get-CfJson @("cloudfront", "create-distribution", "--distribution-config", (Write-JsonFile "dist.json" @{
            CallerReference = (New-CallerReference); Comment = $Name; Enabled = $true
            PriceClass = "PriceClass_100"; HttpVersion = "http2and3"; IsIPV6Enabled = $true
            Origins = @{ Quantity = 1; Items = @($origin) }
            DefaultCacheBehavior = $behavior
        }))).Distribution
    }
    $distId = $dist.Id; $domain = $dist.DomainName
    $distArn = "arn:aws:cloudfront::${account}:distribution/$distId"
    Write-Host "distribution $distId  https://$domain  (status $($dist.Status))"

    # ------------------------------------------------------------ bucket policy (merge our statement)
    Write-Step "Bucket policy: only this distribution may read /$RootFolder/*"
    $sid = "NadlanCloudFrontRead-" + ($RootFolder -replace '[^A-Za-z0-9]', '')
    $statement = [ordered]@{
        Sid = $sid; Effect = "Allow"; Principal = @{ Service = "cloudfront.amazonaws.com" }; Action = "s3:GetObject"
        Resource = "arn:aws:s3:::$Bucket/$RootFolder/*"; Condition = @{ StringEquals = @{ "AWS:SourceArn" = $distArn } }
    }
    $current = Invoke-Aws $AwsProfile @("s3api", "get-bucket-policy", "--bucket", $Bucket) -AllowFailure
    if ($current.Ok) {
        $policy = (($current.Out | ConvertFrom-Json).Policy | ConvertFrom-Json)
        $policy.Statement = @(@($policy.Statement) | Where-Object { $_.Sid -ne $sid }) + $statement
    } else {
        $policy = [ordered]@{ Version = "2012-10-17"; Statement = @($statement) }
    }
    $policyFile = Join-Path $tmpDir "bucket-policy.json"
    [IO.File]::WriteAllText($policyFile, ($policy | ConvertTo-Json -Depth 20), $utf8)
    Invoke-Aws $AwsProfile @("s3api", "put-bucket-policy", "--bucket", $Bucket, "--policy", "file://$policyFile") | Out-Null
    Write-Host "statement $sid set"

    if (-not $SkipWait) {
        Write-Step "Waiting until CloudFront has deployed it everywhere (usually 3-10 minutes)"
        Invoke-Aws $AwsProfile @("cloudfront", "wait", "distribution-deployed", "--id", $distId) | Out-Null
    }

    # ------------------------------------------------------------ self-test (a test file, deleted afterwards)
    Write-Step "Self-test"
    $testRel = "cdn-selftest/" + [guid]::NewGuid().ToString("N") + ".txt"
    $testFile = Join-Path $tmpDir "selftest.txt"
    $body = "GreekPlot CDN self-test " + (Get-Date).ToUniversalTime().ToString("o")
    [IO.File]::WriteAllText($testFile, $body, $utf8)
    Invoke-Aws $AwsProfile @("s3", "cp", $testFile, "s3://$Bucket/$RootFolder/$testRel", "--content-type", "text/plain",
        "--content-disposition", "inline; filename=`"uploader-name.txt`"", "--only-show-errors") | Out-Null
    function Get-Status([string]$Url) {
        try {
            $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 30
            return [pscustomobject]@{ Code = [int]$r.StatusCode; Body = $r.Content; Disposition = $r.Headers["Content-Disposition"]; Cache = $r.Headers["Cache-Control"] }
        } catch [System.Net.WebException] {
            $resp = $_.Exception.Response
            return [pscustomobject]@{ Code = $(if ($resp) { [int]$resp.StatusCode } else { 0 }); Body = ""; Disposition = ""; Cache = "" }
        }
    }
    function New-SignedUrl([string]$Url) {
        $expires = [DateTimeOffset]::UtcNow.AddMinutes(10).ToUnixTimeSeconds()
        $policyText = '{"Statement":[{"Resource":"' + $Url + '","Condition":{"DateLessThan":{"AWS:EpochTime":' + $expires + '}}}]}'
        $pf = Join-Path $tmpDir "policy.txt"; $sf = Join-Path $tmpDir "sig.bin"
        [IO.File]::WriteAllText($pf, $policyText, $utf8)
        $s = Invoke-Native $openssl @("dgst", "-sha1", "-sign", $pemPath, "-out", $sf, $pf)
        if (-not $s.Ok) { throw "openssl could not sign: $($s.Err)" }
        $sig = [Convert]::ToBase64String([IO.File]::ReadAllBytes($sf)).Replace('+', '-').Replace('=', '_').Replace('/', '~')
        $sep = if ($Url.Contains('?')) { '&' } else { '?' }
        return "$Url${sep}Expires=$expires&Signature=$sig&Key-Pair-Id=$publicKeyId"
    }
    $plain = "https://$domain/$testRel"
    $renamed = $plain + "?response-content-disposition=" + [Uri]::EscapeDataString('inline; filename="photo.txt"')
    $checks = @(
        @("signed URL serves the file", { $r = Get-Status (New-SignedUrl $plain); $r.Code -eq 200 -and $r.Body -eq $body }),
        @("browser cache header is set", { $r = Get-Status (New-SignedUrl $plain); "$($r.Cache)" -eq "private, max-age=1800" }),
        @("customer name override reaches S3", { $r = Get-Status (New-SignedUrl $renamed); $r.Code -eq 200 -and "$($r.Disposition)" -match 'photo\.txt' }),
        @("without a signature: refused", { (Get-Status $plain).Code -eq 403 }),
        @("a changed override breaks the signature", { (Get-Status ((New-SignedUrl $renamed).Replace("photo.txt", "other.txt"))).Code -eq 403 }),
        @("_backups refused even when signed", { (Get-Status (New-SignedUrl "https://$domain/_backups/db/x.sql.gz")).Code -eq 403 })
    )
    $failed = 0
    foreach ($c in $checks) {
        $ok = $false
        for ($try = 0; $try -lt 6 -and -not $ok; $try++) { $ok = & $c[1]; if (-not $ok -and $try -lt 5) { Start-Sleep -Seconds 10 } }
        Write-Host ("  {0}  {1}" -f $(if ($ok) { "ok  " } else { "FAIL" }), $c[0]) -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
        if (-not $ok) { $failed++ }
    }
    Invoke-Aws $AwsProfile @("s3", "rm", "s3://$Bucket/$RootFolder/$testRel", "--only-show-errors") | Out-Null
    Write-Host "  test file deleted"
    if ($failed) { throw "$failed self-test check(s) failed - the server settings were not changed." }

    # ------------------------------------------------------------ server settings
    $pemOneLine = ((Get-Content -Raw $pemPath) -replace "`r", "").Trim() -replace "`n", '\n'
    Write-Step "Server settings (app_config ms:host)"
    Write-Host "  Nadlan:Storage:Delivery:Mode                 = CloudFrontSigned"
    Write-Host "  Nadlan:Storage:Delivery:CloudFrontDomain     = $domain"
    Write-Host "  Nadlan:Storage:Delivery:CloudFrontOriginPath = /$RootFolder"
    Write-Host "  Nadlan:Storage:Delivery:KeyPairId            = $publicKeyId"
    Write-Host "  Nadlan:Storage:Delivery:PrivateKeyPem        = (the key in $pemPath, one line)"
    Write-Host "Rollback: Mode = S3Presigned (the CDN can stay; nothing else changes)."
    if (Read-YesNo "Apply these on the server now over SSH and restart the app?" $false) {
        $target = Get-SshTarget "" "" ""
        $server = "/opt/nadlan/current/server"
        foreach ($pair in @(@("Nadlan:Storage:Delivery:CloudFrontDomain", $domain), @("Nadlan:Storage:Delivery:CloudFrontOriginPath", "/$RootFolder"),
                @("Nadlan:Storage:Delivery:KeyPairId", $publicKeyId), @("Nadlan:Storage:Delivery:PrivateKeyPem", $pemOneLine),
                @("Nadlan:Storage:Delivery:Mode", "CloudFrontSigned"))) {
            Invoke-RemoteWithInput $target "sudo $server/config-set.sh ms:host $($pair[0])" $pair[1]
        }
        Invoke-Remote $target "sudo systemctl restart nadlan && for i in `$(seq 1 30); do curl -fsS --max-time 3 http://127.0.0.1:5515/api/health 2>/dev/null && exit 0; sleep 2; done; echo 'No healthy answer after 60 s - set Mode back to S3Presigned (7-server-admin) and see the log.'; exit 1"
    }
    Write-Host ""
    Write-Host "CDN ready: https://$domain (distribution $distId)" -ForegroundColor Green
}
finally {
    Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue
}

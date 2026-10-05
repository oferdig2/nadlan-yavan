<#
.SYNOPSIS
Builds the KAEK polygon importer for Windows and Mac (Apple Silicon + Intel), for Nadlan admins.

.DESCRIPTION
Asks for the Nadlan address the importer should talk to and a version, then builds (via installer\Publish-KaekImporter.ps1)
self-contained packages into code\dist - the admin's PC needs no .NET:
  NadlanKaekImporter-<version>-windows.zip               extract, run Install.cmd (per user, no admin rights)
  NadlanKaekImporter-<version>-mac-apple-silicon.tar.gz  M1-M4 Macs: unpack, drag to Applications, approve once
  NadlanKaekImporter-<version>-mac-intel.tar.gz          Intel Macs: same
plus a .sha256 per package and NadlanKaekImporter-<version>-README.txt (what to send to whom).
Next: 2-upload-importer-s3.ps1 puts them on S3 and prints download links.
#>
param(
    [string]$ApiUrl = "",
    [string]$Version = ""
)
. (Join-Path $PSScriptRoot "lib\common.ps1")

$defaultUrl = Get-Setting "publicUrl"
$ApiUrl = Read-Value "Nadlan address the importer connects to" $ApiUrl -Setting "importerApiUrl" -Default $defaultUrl `
    -Pattern '^https?://[A-Za-z0-9.-]+(:\d+)?/?$' -PatternHint "Like https://nadlan.example.com"
$ApiUrl = $ApiUrl.TrimEnd('/')
if ($ApiUrl -like "http://*" -and $ApiUrl -notmatch '^http://(localhost|127\.)') {
    Write-Warning "Plain http: the importer's sign-in token would travel unencrypted. Use the https address once the server has a certificate."
}

# Suggest the next patch after the newest version built here (or remembered).
if (-not $Version) {
    $known = @(Get-ChildItem $DistRoot -File -Filter "NadlanKaekImporter-*" -ErrorAction SilentlyContinue |
        ForEach-Object { if ($_.Name -match '^NadlanKaekImporter-(\d+\.\d+\.\d+)-') { [version]$Matches[1] } })
    $last = Get-Setting "importerVersion"
    if ($last -match '^\d+\.\d+\.\d+$') { $known += [version]$last }
    $newest = $known | Sort-Object -Descending | Select-Object -First 1
    $suggest = if ($newest) { "$($newest.Major).$($newest.Minor).$($newest.Build + 1)" } else { "1.0.0" }
    $Version = Read-Value "Importer version" -Default $suggest -Pattern '^\d+\.\d+\.\d+$' -PatternHint "Like 1.0.3"
}
Set-Setting "importerVersion" $Version

$targets = @("windows", "mac-apple-silicon", "mac-intel")
if (-not (Read-YesNo "Build all three (Windows, Mac Apple Silicon, Mac Intel)?" $true)) {
    $targets = @($targets | Where-Object { Read-YesNo "  build $_?" $true })
    if ($targets.Count -eq 0) { throw "Nothing to build." }
}

Write-Step "Building importer $Version for $ApiUrl"
& (Join-Path $CodeRoot "installer\Publish-KaekImporter.ps1") -ApiUrl $ApiUrl -Version $Version -Targets $targets

Write-Step "Checksums and README"
$packages = @(Get-ChildItem $DistRoot -File | Where-Object { $_.Name -match "^NadlanKaekImporter-$([regex]::Escape($Version))-.+\.(zip|tar\.gz)$" } | Sort-Object Name)
$sums = @()
foreach ($p in $packages) {
    $hash = (Get-FileHash -LiteralPath $p.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$($p.FullName).sha256", "$hash  $($p.Name)`n")
    $sums += ("  {0}  {1,7:N1} MB  sha256 {2}" -f $p.Name, ($p.Length / 1MB), $hash)
}

$readme = @"
Nadlan KAEK Importer $Version - for Nadlan admins
================================================
Imports parcel polygons from the Greek Cadastre map (gis.ktimanet.gr) into Nadlan at:
    $ApiUrl
The user must have a Nadlan account that may create Parcels.

Which file
- Windows 10/11 (64-bit):        NadlanKaekImporter-$Version-windows.zip
- Mac with Apple M1-M4 chip:     NadlanKaekImporter-$Version-mac-apple-silicon.tar.gz
- Mac with Intel processor:      NadlanKaekImporter-$Version-mac-intel.tar.gz
  (Apple menu > About This Mac: "Chip Apple M..." = Apple Silicon, "Processor Intel" = Intel)

Install
- Windows: right-click the zip > Extract All, open the folder, double-click Install.cmd.
  If "Windows protected your PC" appears: More info > Run anyway. No administrator rights needed.
- Mac: double-click the .tar.gz, drag "Nadlan KAEK Importer.app" to Applications, open it.
  The first time macOS blocks it: System Settings > Privacy & Security > "Open Anyway" (macOS 13-14: right-click > Open).
Each package has INSTALL.txt with the full steps.

First start
The panel shows "Connect to Nadlan": sign in with email and password in the tab that opens and click Connect.
The importer remembers the connection.

Files
$($sums -join "`n")
Check a download: Windows  certutil -hashfile <file> SHA256    Mac  shasum -a 256 <file>
"@
$readmePath = Join-Path $DistRoot "NadlanKaekImporter-$Version-README.txt"
[IO.File]::WriteAllText($readmePath, ($readme -replace "`r`n", "`n"))

Write-Host ""
Write-Host "Importer $Version for $ApiUrl is in $DistRoot" -ForegroundColor Green
$sums | ForEach-Object { Write-Host $_ }
Write-Host "Next: 2-upload-importer-s3.ps1"

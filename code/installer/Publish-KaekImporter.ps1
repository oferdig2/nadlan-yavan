# Builds the customer install packages of GreekPlot KAEK Importer into code\dist:
#   NadlanKaekImporter-<version>-windows.zip          extract, run Install.cmd (per user, no admin)
#   NadlanKaekImporter-<version>-mac-apple-silicon.tar.gz   M1/M2/M3/M4 Macs
#   NadlanKaekImporter-<version>-mac-intel.tar.gz            older Intel Macs
# Each package carries importer.json with the GreekPlot address its users should talk to.
#
# Usage:  .\Publish-KaekImporter.ps1 -ApiUrl https://nadlan.example.com -Version 1.0.0
param(
    [string]$ApiUrl = "http://localhost:5515",
    [string]$Version = "1.0.0",
    [ValidateSet("windows", "mac-apple-silicon", "mac-intel")]
    [string[]]$Targets = @("windows", "mac-apple-silicon", "mac-intel")
)
$ErrorActionPreference = "Stop"

$code = Split-Path -Parent $PSScriptRoot
$project = Join-Path $code "src\Nadlan.KaekImporter\Nadlan.KaekImporter.csproj"
$dist = Join-Path $code "dist"
$work = Join-Path $dist "work"
$appName = "GreekPlot KAEK Importer"
$rids = @{ "windows" = "win-x64"; "mac-apple-silicon" = "osx-arm64"; "mac-intel" = "osx-x64" }
# Windows' own tar (bsdtar) - it can set Unix file modes, which a Mac needs to run the app.
$tar = Join-Path $env:SystemRoot "System32\tar.exe"

function Publish-App([string]$rid, [string]$output) {
    Write-Host "Building $rid ..." -ForegroundColor Cyan
    # Out-Host: a function returns everything written to its output, and callers expect just the package path.
    & dotnet publish $project -c Release -r $rid --self-contained -p:Version=$Version -o $output --nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }
    $settings = [ordered]@{ apiUrl = $ApiUrl }
    $settings | ConvertTo-Json | Set-Content -Path (Join-Path $output "importer.json") -Encoding ASCII
}

function Get-LfText([string]$path) {
    # Shell scripts must have Unix line endings, whatever git did on checkout.
    return ([IO.File]::ReadAllText($path) -replace "`r`n", "`n")
}

# mtree names: relative, "./"-prefixed, spaces and other specials as octal escapes.
function Get-MtreeName([string]$path) {
    $sb = New-Object Text.StringBuilder
    foreach ($ch in $path.ToCharArray()) {
        if ($ch -eq ' ' -or $ch -eq '#' -or $ch -eq '\' -or [int]$ch -gt 126 -or [int]$ch -lt 33) {
            foreach ($b in [Text.Encoding]::UTF8.GetBytes([string]$ch)) { [void]$sb.Append('\' + [Convert]::ToString($b, 8).PadLeft(3, '0')) }
        }
        else { [void]$sb.Append($ch) }
    }
    return $sb.ToString()
}

function New-MacPackage([string]$target, [string]$rid) {
    $publish = Join-Path $work $rid
    Publish-App $rid $publish

    $root = Join-Path $work "$target-root"
    $app = Join-Path $root "$appName.app"
    $macos = Join-Path $app "Contents\MacOS"
    New-Item -ItemType Directory -Force $macos | Out-Null
    Copy-Item (Join-Path $publish "*") $macos -Recurse -Force
    $plist = (Get-Content (Join-Path $PSScriptRoot "mac\Info.plist") -Raw).Replace("{VERSION}", $Version)
    [IO.File]::WriteAllText((Join-Path $app "Contents\Info.plist"), $plist)
    [IO.File]::WriteAllText((Join-Path $macos "nadlan-launcher"), (Get-LfText (Join-Path $PSScriptRoot "mac\nadlan-launcher")))
    Copy-Item (Join-Path $PSScriptRoot "mac\INSTALL.txt") $root

    # Programs the Mac runs directly need the executable bit; everything else is plain data.
    $executables = @("nadlan-launcher", "NadlanKaekImporter", "createdump", "node")
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $lines = New-Object Collections.Generic.List[string]
    $lines.Add("#mtree")
    foreach ($item in Get-ChildItem $root -Recurse -Force) {
        $relative = "./" + $item.FullName.Substring($root.Length + 1).Replace('\', '/')
        if ($item.PSIsContainer) {
            $lines.Add("$(Get-MtreeName $relative) type=dir mode=0755 time=$now.0")
        }
        else {
            $mode = if ($executables -contains $item.Name) { "0755" } else { "0644" }
            $contents = Get-MtreeName ($item.FullName.Replace('\', '/'))
            $lines.Add("$(Get-MtreeName $relative) type=file mode=$mode time=$now.0 contents=$contents")
        }
    }
    $spec = Join-Path $work "$target.mtree"
    [IO.File]::WriteAllLines($spec, $lines)

    $package = Join-Path $dist "NadlanKaekImporter-$Version-$target.tar.gz"
    Push-Location $work
    try {
        & $tar -czf $package "@$target.mtree"
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $target" }
    }
    finally { Pop-Location }
    return $package
}

function New-WindowsPackage {
    $root = Join-Path $work "windows-root"
    Publish-App "win-x64" (Join-Path $root "app")
    foreach ($file in "Install.cmd", "install.ps1", "uninstall.ps1", "INSTALL.txt") {
        Copy-Item (Join-Path $PSScriptRoot "windows\$file") $root
    }

    $package = Join-Path $dist "NadlanKaekImporter-$Version-windows.zip"
    if (Test-Path $package) { Remove-Item $package -Force }
    Compress-Archive -Path (Join-Path $root "*") -DestinationPath $package -CompressionLevel Optimal
    return $package
}

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null

$built = @()
foreach ($target in $Targets) {
    $package = if ($target -eq "windows") { New-WindowsPackage } else { New-MacPackage $target $rids[$target] }
    $built += Get-Item $package
}

Remove-Item $work -Recurse -Force
Write-Host ""
Write-Host "Packages for GreekPlot at $ApiUrl (version $Version):" -ForegroundColor Green
$built | ForEach-Object { Write-Host ("  {0}  ({1:N0} MB)" -f $_.FullName, ($_.Length / 1MB)) }

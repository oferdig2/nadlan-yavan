# Installs GreekPlot KAEK Importer for the current user: no administrator rights needed.
# Copies the program to %LOCALAPPDATA%\Programs, adds Start Menu and desktop shortcuts, and registers an
# uninstaller in Settings > Apps. Run through Install.cmd, from the extracted zip.
$ErrorActionPreference = "Stop"

$name = "GreekPlot KAEK Importer"
$source = Join-Path $PSScriptRoot "app"
$target = Join-Path $env:LOCALAPPDATA "Programs\$name"
$exe = Join-Path $target "NadlanKaekImporter.exe"

try {
    if (-not (Test-Path (Join-Path $source "NadlanKaekImporter.exe"))) {
        throw "The program files were not found next to this installer. Extract the whole zip first (right-click > Extract All), then run Install.cmd from the extracted folder."
    }

    $running = Get-Process NadlanKaekImporter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
    if ($running) {
        throw "$name is running. Close its browser window, then run Install.cmd again."
    }

    # The product was called "Nadlan KAEK Importer" before: remove that install (folder and shortcuts) so only one
    # remains. The user's settings, token and reports stay where they are (%LOCALAPPDATA%\Nadlan, Documents\Nadlan).
    $oldName = "Nadlan KAEK Importer"
    $oldTarget = Join-Path $env:LOCALAPPDATA "Programs\$oldName"
    if (Get-Process NadlanKaekImporter -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$oldTarget\*" }) {
        throw "$oldName (the previous name of this program) is running. Close its browser window, then run Install.cmd again."
    }
    foreach ($old in @(
            (Join-Path ([Environment]::GetFolderPath("Programs")) "$oldName.lnk"),
            (Join-Path ([Environment]::GetFolderPath("Desktop")) "$oldName.lnk"),
            $oldTarget)) {
        if (Test-Path $old) { Remove-Item $old -Recurse -Force }
    }

    Write-Host "Installing $name to $target ..."
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $source "*") $target -Recurse -Force
    # Files from a downloaded zip carry "downloaded from the internet"; clear it so Windows asks only once.
    Get-ChildItem $target -Recurse -File | Unblock-File
    $version = (Get-Item $exe).VersionInfo.ProductVersion

    $uninstall = Join-Path $target "uninstall.ps1"
    Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") $uninstall -Force

    $shell = New-Object -ComObject WScript.Shell
    $shortcuts = @(
        (Join-Path ([Environment]::GetFolderPath("Programs")) "$name.lnk"),
        (Join-Path ([Environment]::GetFolderPath("Desktop")) "$name.lnk")
    )
    foreach ($path in $shortcuts) {
        $link = $shell.CreateShortcut($path)
        $link.TargetPath = $exe
        $link.WorkingDirectory = $target
        $link.Description = "Import parcels from the Greek Cadastre map into GreekPlot"
        $link.Save()
    }

    # Settings > Apps > Installed apps entry, per user.
    $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NadlanKaekImporter"
    New-Item -Path $key -Force | Out-Null
    $values = @{
        DisplayName     = $name
        DisplayVersion  = "$version"
        Publisher       = "GreekPlot"
        InstallLocation = $target
        DisplayIcon     = $exe
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstall`""
    }
    foreach ($entry in $values.GetEnumerator()) {
        New-ItemProperty -Path $key -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null
    }
    New-ItemProperty -Path $key -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $key -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

    Write-Host ""
    Write-Host "$name $version is installed. Start it from the Start Menu or the desktop shortcut." -ForegroundColor Green
    Write-Host "Your own settings (e.g. a different GreekPlot address) go in $env:LOCALAPPDATA\Nadlan\importer.json"
    Write-Host ""
    Read-Host "Press Enter to close"
}
catch {
    Write-Host ""
    Write-Host "Installation failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

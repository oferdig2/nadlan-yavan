# Removes Nadlan KAEK Importer for the current user (started from Settings > Apps).
# Keeps %LOCALAPPDATA%\Nadlan (the user's settings, logs) and Documents\Nadlan (import reports).
$ErrorActionPreference = "Stop"

$name = "Nadlan KAEK Importer"
$target = Join-Path $env:LOCALAPPDATA "Programs\$name"

Get-Process NadlanKaekImporter -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$target\*" } |
    Stop-Process -Force

foreach ($path in @(
        (Join-Path ([Environment]::GetFolderPath("Programs")) "$name.lnk"),
        (Join-Path ([Environment]::GetFolderPath("Desktop")) "$name.lnk"))) {
    if (Test-Path $path) { Remove-Item $path -Force }
}

Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NadlanKaekImporter" -Recurse -Force -ErrorAction SilentlyContinue

# This script lives in the folder it removes: delete the folder from a separate process after it exits.
Start-Process cmd.exe -WindowStyle Hidden -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$target`""
Write-Host "$name was removed."

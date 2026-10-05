<#
.SYNOPSIS
Menu of the Nadlan release scripts: pick one, read what it does, run it.

.DESCRIPTION
Lists the numbered scripts in this folder with their synopsis. Each script asks for what it needs and suggests your
previous answers (kept in release.local.json next to this file; no secrets are stored).
#>
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Get-ScriptHelp([string]$Path) {
    $text = Get-Content -LiteralPath $Path -Raw
    $m = [regex]::Match($text, '^\s*<#([\s\S]*?)#>')
    if (-not $m.Success) { return @{ Synopsis = "(no description)"; Body = "" } }
    $body = $m.Groups[1].Value
    $syn = [regex]::Match($body, '\.SYNOPSIS\s*\r?\n\s*(.+)')
    return @{ Synopsis = $(if ($syn.Success) { $syn.Groups[1].Value.Trim() } else { "(no description)" }); Body = $body }
}

while ($true) {
    $scripts = @(Get-ChildItem -LiteralPath $root -File -Filter "*.ps1" | Where-Object { $_.Name -match '^\d+-' } | Sort-Object Name)
    Write-Host ""
    Write-Host "Nadlan release scripts" -ForegroundColor Cyan
    for ($i = 0; $i -lt $scripts.Count; $i++) {
        Write-Host ("  [{0}] {1,-28} {2}" -f ($i + 1), $scripts[$i].BaseName, (Get-ScriptHelp $scripts[$i].FullName).Synopsis)
    }
    Write-Host "  [q] quit"
    $sel = "$(Read-Host "Choose")".Trim()
    if ($sel -match '^(q|quit|exit)$') { return }
    if ($sel -notmatch '^\d+$' -or [int]$sel -lt 1 -or [int]$sel -gt $scripts.Count) { Write-Host "Enter a number from the list." -ForegroundColor Yellow; continue }

    $script = $scripts[[int]$sel - 1]
    $help = Get-ScriptHelp $script.FullName
    Write-Host ""
    Write-Host $script.Name -ForegroundColor Cyan
    Write-Host (($help.Body -replace '(?m)^\.(SYNOPSIS|DESCRIPTION|NOTES)\s*$', '').Trim())
    Write-Host ""
    $go = "$(Read-Host "Run it? (Y/n)")".Trim().ToLowerInvariant()
    if ($go -and $go -notmatch '^(y|yes)$') { continue }

    try {
        & $script.FullName
        Write-Host ""
        Write-Host "$($script.BaseName): done." -ForegroundColor Green
    }
    catch {
        Write-Host ""
        Write-Host "$($script.BaseName) stopped: $($_.Exception.Message)" -ForegroundColor Red
    }
}

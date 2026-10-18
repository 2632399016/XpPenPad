# Deploy: stop resident, copy plugin to Xournal++ plugin dir, restart resident.
# Portable: resolves repo root from this script's location.
$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repo = Split-Path -Parent $scriptDir
$dist = Join-Path $repo "XpPenPad\dist"
$src = Join-Path $repo "plugins\drawboard-pen"
$dest = Join-Path $env:LOCALAPPDATA "xournalpp\plugins\drawboard-pen"

# 1. resident must die first (it locks the exe)
Get-Process XpPenPad -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output ("kill resident pid=" + $_.Id)
    Stop-Process -Id $_.Id -Force -Confirm:$false
}
Start-Sleep -Milliseconds 500

# 2. plugin files
if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
Copy-Item (Join-Path $src "*") $dest -Recurse -Force
Write-Output ("plugin -> " + $dest)

# 3. start resident
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $dist "XpPenPad.exe"
$psi.WorkingDirectory = $dist
$psi.UseShellExecute = $true
$psi.WindowStyle = "Hidden"
[void][System.Diagnostics.Process]::Start($psi)

Start-Sleep -Seconds 3
$rp = @(Get-Process XpPenPad -ErrorAction SilentlyContinue)
Write-Output ("resident running = " + ($rp.Count -gt 0))

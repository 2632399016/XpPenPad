# Smoke test: run self-test, calib, verify resident is alive.
# Portable: resolves repo root from this script's location.
$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repo = Split-Path -Parent $scriptDir
$dir = Join-Path $repo "XpPenPad\dist"
$log = Join-Path $scriptDir "smoke.log"
$out = New-Object System.Collections.ArrayList

# 1) self-test
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $dir "XpPenPad.exe"
$psi.Arguments = "--test"
$psi.WorkingDirectory = $dir
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$p = [System.Diagnostics.Process]::Start($psi)
$so = $p.StandardOutput.ReadToEnd()
$se = $p.StandardError.ReadToEnd()
$p.WaitForExit()
[void]$out.Add("TEST exitcode=" + $p.ExitCode)
if ($so) { [void]$out.Add("TEST stdout: " + $so) }
if ($se) { [void]$out.Add("TEST stderr: " + $se) }

# 2) calib smoke (3s), capture exit + csv
$csv = Join-Path $dir "raw_calib.csv"
$csvBefore = if (Test-Path $csv) { (Get-Item $csv).LastWriteTime } else { [datetime]::MinValue }
$psi2 = New-Object System.Diagnostics.ProcessStartInfo
$psi2.FileName = Join-Path $dir "XpPenPad.exe"
$psi2.Arguments = "--calib 3"
$psi2.WorkingDirectory = $dir
$psi2.UseShellExecute = $false
$psi2.RedirectStandardOutput = $true
$psi2.RedirectStandardError = $true
$p2 = [System.Diagnostics.Process]::Start($psi2)
if (-not $p2.WaitForExit(15000)) {
    $p2.Kill()
    [void]$out.Add("CALIB TIMEOUT(killed)")
} else {
    [void]$out.Add("CALIB exitcode=" + $p2.ExitCode)
    $e2 = $p2.StandardError.ReadToEnd()
    if ($e2) { [void]$out.Add("CALIB stderr: " + $e2) }
}
if (Test-Path $csv) {
    $lm = (Get-Item $csv).LastWriteTime
    if ($lm -gt $csvBefore) { [void]$out.Add("CSV ok size=" + (Get-Item $csv).Length + " mtime=" + $lm) }
    else { [void]$out.Add("CSV STALE (calib did not write) size=" + (Get-Item $csv).Length) }
} else { [void]$out.Add("CSV MISSING") }

# 3) resident still alive?
$rp = @(Get-Process XpPenPad -ErrorAction SilentlyContinue)
[void]$out.Add("resident running = " + ($rp.Count -gt 0))

$out -join "`r`n" | Set-Content $log -Encoding UTF8
Write-Output ("smoke log -> " + $log)

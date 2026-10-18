# Build the self-contained single-file app.
# Portable: resolves repo root from this script's location.
# Requires .NET 9 SDK on PATH, or set $env:DOTNET to the dotnet.exe path.
$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repo = Split-Path -Parent $scriptDir
$proj = Join-Path $repo "XpPenPad\XpPenPad.csproj"
$dist = Join-Path $repo "XpPenPad\dist"
$layout = Join-Path $repo "XpPenPad\bin\Release\net9.0-windows\win-x64"

$dotnet = if ($env:DOTNET) { $env:DOTNET } else { "dotnet" }

# The resident instance locks the exe; publish would fail with "file in use".
Get-Process XpPenPad -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output ("kill resident pid=" + $_.Id)
    Stop-Process -Id $_.Id -Force -Confirm:$false
}
Start-Sleep -Milliseconds 400

& $dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o $dist
$rc = $LASTEXITCODE
Write-Output ("publish exit=" + $rc)

if ($rc -eq 0) {
    Copy-Item (Join-Path $repo "XpPenPad\xpPenPad.ini") (Join-Path $dist "xpPenPad.ini") -Force
    Write-Output "ini copied"
    # WPF native DLLs are NOT inside the single-file bundle; without these loose
    # files next to the exe the app dies with DllNotFoundException (0xC0000135).
    $natives = @("wpfgfx_cor3.dll","PresentationNative_cor3.dll","PenImc_cor3.dll","vcruntime140_cor3.dll","D3DCompiler_47_cor3.dll")
    foreach ($n in $natives) {
        $src = Join-Path $layout $n
        if (Test-Path $src) { Copy-Item $src (Join-Path $dist $n) -Force; Write-Output ("native copied: " + $n) }
        else { Write-Output ("NATIVE MISSING: " + $n) }
    }
}
exit $rc

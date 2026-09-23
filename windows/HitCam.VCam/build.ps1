# Builds HitCamVCam.dll (x64, Release) with the CMake that ships with Visual Studio or one from PATH.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $cmake = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -find "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" | Select-Object -First 1
    }
}
if (-not $cmake) { throw "CMake not found: install Visual Studio with 'Desktop development with C++'." }

& $cmake -S $root -B "$root\build" -A x64
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& $cmake --build "$root\build" --config $Configuration
exit $LASTEXITCODE

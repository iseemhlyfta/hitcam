# Builds HitCamVCam.dll and the DirectShow camera (HitCamDShow.dll x64, HitCamDShow32.dll x86), Release, with the
# CMake that ships with Visual Studio or one from PATH.
#   build.ps1          the DLLs only (what the HitCam.Desktop build runs)
#   build.ps1 -Tests   the DLLs and the HitCamVCamTest.exe check harness
param([string]$Configuration = "Release", [switch]$Tests)
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
$targets = @("HitCamVCam", "HitCamDShow")
if ($Tests) { $targets += "HitCamVCamTest" }
& $cmake --build "$root\build" --config $Configuration --target $targets
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# 32-bit apps load only a 32-bit DirectShow camera.
& $cmake -S $root -B "$root\build32" -A Win32 -DHITCAM_DSHOW_ONLY=ON
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& $cmake --build "$root\build32" --config $Configuration --target HitCamDShow
exit $LASTEXITCODE

# Installs HitCam for the current user: builds it, copies it to %LOCALAPPDATA%\Programs\HitCam,
# adds Start menu and desktop shortcuts and an entry in Settings > Apps. No admin rights needed.
#   powershell -ExecutionPolicy Bypass -File windows\install.ps1              install or update
#   powershell -ExecutionPolicy Bypass -File windows\install.ps1 -Uninstall   remove
param([switch]$Uninstall, [switch]$NoDesktopShortcut)
$ErrorActionPreference = "Stop"

$appName = "HitCam"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\HitCam"
$exe = Join-Path $installDir "HitCam.exe"
$startMenuLink = Join-Path ([Environment]::GetFolderPath("Programs")) "HitCam.lnk"
$desktopLink = Join-Path ([Environment]::GetFolderPath("Desktop")) "HitCam.lnk"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HitCam"

function Stop-InstalledApp {
    Get-Process HitCam -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            $_.CloseMainWindow() | Out-Null
            if (-not $_.WaitForExit(5000)) { $_.Kill() }
        }
}

if ($Uninstall) {
    Stop-InstalledApp
    Remove-Item $startMenuLink, $desktopLink -ErrorAction SilentlyContinue
    Remove-Item $uninstallKey -Recurse -ErrorAction SilentlyContinue
    # This script may run from the install folder itself: delete the folder once it has exited.
    Start-Process cmd.exe -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$installDir`"" -WindowStyle Hidden
    $cameraDir = Join-Path $env:ProgramFiles "HitCam"
    Write-Host "HitCam removed. The virtual camera stays registered; to remove it too (as admin), in $cameraDir"
    Write-Host "  Windows 11: regsvr32 /u HitCamVCam-<hash>.dll"
    Write-Host "  Windows 10: regsvr32 /u HitCamDShow-<hash>.dll and $env:WINDIR\SysWOW64\regsvr32 /u HitCamDShow32-<hash>.dll"
    Write-Host "then delete the folder."
    return
}

$project = Join-Path $PSScriptRoot "HitCam.Desktop"
if (-not (Test-Path $project)) { throw "Run this script from the repository (windows\install.ps1)." }

Write-Host "Building HitCam..."
# Separate temporary build and publish folders: HitCam copies started from the repository's bin\ folders lock their
# files and must not break the installation, and the installed copy keeps running until the build has succeeded.
$buildDir = Join-Path $env:TEMP "HitCam-install-build"
$publishDir = Join-Path $env:TEMP "HitCam-install-publish"
try {
    Remove-Item -LiteralPath $buildDir, $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publishDir `
        --artifacts-path $buildDir --nologo -v q
    if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

    Stop-InstalledApp
    # Replace the installation as a whole, so files an older version shipped do not linger.
    if (Test-Path -LiteralPath $installDir) {
        Get-ChildItem -LiteralPath $installDir -Force | Remove-Item -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $installDir -Recurse -Force
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $installDir "install.ps1") -Force

    # Object detection models are not in git (release builds get them from CI): take the ones exported locally.
    $models = Get-ChildItem -Path (Join-Path $PSScriptRoot "..\vision\output\models") -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^rfdetr-(nano|small)\.(onnx|labels\.json)$' }
    if ($models) {
        $modelsDir = New-Item -ItemType Directory -Force -Path (Join-Path $installDir "models")
        $models | Copy-Item -Destination $modelsDir -Force
    } else {
        Write-Host "No object detection model found. To enable object detection, run from the vision folder:"
        Write-Host "  python export_default.py --out $installDir\models   (see vision\README.md)"
    }

    # Hand and face models: from vision\output if there, otherwise downloaded (checked by SHA-256).
    try {
        & (Join-Path $PSScriptRoot "get-models.ps1") -Destination (Join-Path $installDir "models")
    }
    catch {
        Write-Host "Hand and face models could not be downloaded ($($_.Exception.Message)); those features stay off."
        Write-Host "  Run windows\get-models.ps1 -Destination $installDir\models later to add them."
    }
}
finally {
    Remove-Item -LiteralPath $buildDir, $publishDir -Recurse -Force -ErrorAction SilentlyContinue
}

$shell = New-Object -ComObject WScript.Shell
$links = @($startMenuLink)
if (-not $NoDesktopShortcut) { $links += $desktopLink }
foreach ($path in $links) {
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $installDir
    $link.IconLocation = "$exe,0"
    $link.Description = "Phone camera as a webcam"
    $link.Save()
}

$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
New-Item $uninstallKey -Force | Out-Null
$values = @{
    DisplayName = $appName
    DisplayIcon = "$exe,0"
    DisplayVersion = $version
    Publisher = "iseemhlyfta"
    InstallLocation = $installDir
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$installDir\install.ps1`" -Uninstall"
}
foreach ($name in $values.Keys) { Set-ItemProperty $uninstallKey -Name $name -Value $values[$name] }
Set-ItemProperty $uninstallKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty $uninstallKey -Name NoRepair -Value 1 -Type DWord

Write-Host "HitCam $version installed to $installDir"
Write-Host "Start it from the Start menu or the desktop shortcut."

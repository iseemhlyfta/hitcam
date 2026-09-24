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
    Write-Host "HitCam removed. The virtual camera stays registered; to remove it too (as admin):"
    Write-Host "  regsvr32 /u `"$env:ProgramFiles\HitCam\HitCamVCam.dll`""
    return
}

$project = Join-Path $PSScriptRoot "HitCam.Desktop"
if (-not (Test-Path $project)) { throw "Run this script from the repository (windows\install.ps1)." }

Stop-InstalledApp
Write-Host "Building HitCam..."
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $installDir --nologo -v q
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
Copy-Item $PSCommandPath (Join-Path $installDir "install.ps1") -Force

$shell = New-Object -ComObject WScript.Shell
$links = @($startMenuLink)
if (-not $NoDesktopShortcut) { $links += $desktopLink }
foreach ($path in $links) {
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $installDir
    $link.IconLocation = "$exe,0"
    $link.Description = "iPhone camera as a webcam"
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

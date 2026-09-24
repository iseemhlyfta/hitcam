# Puts the hand tracking models (MediaPipe Hands by Google, converted to ONNX by OpenCV Zoo; Apache 2.0) into a folder.
# They are not in git: files already in -Cache are used, otherwise they are downloaded from a fixed OpenCV Zoo commit.
# Every file is checked against its SHA-256.
#   powershell -ExecutionPolicy Bypass -File windows\get-hand-models.ps1 -Destination <folder>
param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [string]$Cache
)
$ErrorActionPreference = "Stop"
# The progress bar of Windows PowerShell 5.1 slows Invoke-WebRequest down many times over.
$ProgressPreference = "SilentlyContinue"
# Windows PowerShell 5.1 has no $PSScriptRoot in parameter defaults.
if (-not $Cache) { $Cache = Join-Path $PSScriptRoot "..\vision\output\hands" }

$commit = "47534e27c9851bb1128ccc0102f1145e27f23f98"
$models = @(
    @{ Name = "palm_detection_mediapipe_2023feb.onnx"; Folder = "palm_detection_mediapipe";
       Sha256 = "78ff51c38496b7fc8b8ebdb6cc8c1abb02fa6c38427c6848254cdaba57fcce7c" },
    @{ Name = "handpose_estimation_mediapipe_2023feb.onnx"; Folder = "handpose_estimation_mediapipe";
       Sha256 = "db0898ae717b76b075d9bf563af315b29562e11f8df5027a1ef07b02bef6d81c" }
)

function Test-Model([string]$path, [string]$sha256) {
    (Test-Path -LiteralPath $path) -and ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $sha256)
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
# Windows PowerShell 5.1 does not offer TLS 1.2 by default.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

foreach ($model in $models) {
    $target = Join-Path $Destination $model.Name
    if (Test-Model $target $model.Sha256) { continue }

    $cached = Join-Path $Cache $model.Name
    if (Test-Model $cached $model.Sha256) {
        Copy-Item -LiteralPath $cached -Destination $target -Force
        continue
    }

    # Git LFS files are served from media.githubusercontent.com, not raw.githubusercontent.com.
    $url = "https://media.githubusercontent.com/media/opencv/opencv_zoo/$commit/models/$($model.Folder)/$($model.Name)"
    Write-Host "Downloading $($model.Name)..."
    $partial = "$target.part"
    Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing
    if (-not (Test-Model $partial $model.Sha256)) {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        throw "$($model.Name): the download does not match its SHA-256"
    }
    Move-Item -LiteralPath $partial -Destination $target -Force
}
Write-Host "Hand models are in $Destination"

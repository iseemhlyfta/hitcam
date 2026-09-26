# Puts the hand, face and segmentation models into <Destination>\hands, \faces and \segment. They are not in git:
# files already in the cache (vision\output\<set>) are used, otherwise they are downloaded from a fixed commit.
# Every file is checked against its SHA-256.
#   Hands: MediaPipe Hands (Google), converted to ONNX by OpenCV Zoo; Apache 2.0.
#   Faces: YuNet (MIT) and SFace (Apache 2.0), OpenCV Zoo.
#   Segment: MediaPipe Selfie Segmentation (Google), converted to ONNX by onnx-community (Hugging Face); Apache 2.0.
#   powershell -ExecutionPolicy Bypass -File windows\get-models.ps1 -Destination <models folder>
param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [string]$Cache
)
$ErrorActionPreference = "Stop"
# The progress bar of Windows PowerShell 5.1 slows Invoke-WebRequest down many times over.
$ProgressPreference = "SilentlyContinue"
# Windows PowerShell 5.1 has no $PSScriptRoot in parameter defaults.
if (-not $Cache) { $Cache = Join-Path $PSScriptRoot "..\vision\output" }

# OpenCV Zoo keeps its models in Git LFS, served from media.githubusercontent.com, not raw.githubusercontent.com.
$zoo = "https://media.githubusercontent.com/media/opencv/opencv_zoo/47534e27c9851bb1128ccc0102f1145e27f23f98/models"
$selfie = "https://huggingface.co/onnx-community/mediapipe_selfie_segmentation/resolve/be49485c8e027524be38591817fc5cd31bd9d00e"
$models = @(
    @{ Set = "hands"; Name = "palm_detection_mediapipe_2023feb.onnx";
       Url = "$zoo/palm_detection_mediapipe/palm_detection_mediapipe_2023feb.onnx";
       Sha256 = "78ff51c38496b7fc8b8ebdb6cc8c1abb02fa6c38427c6848254cdaba57fcce7c" },
    @{ Set = "hands"; Name = "handpose_estimation_mediapipe_2023feb.onnx";
       Url = "$zoo/handpose_estimation_mediapipe/handpose_estimation_mediapipe_2023feb.onnx";
       Sha256 = "db0898ae717b76b075d9bf563af315b29562e11f8df5027a1ef07b02bef6d81c" },
    @{ Set = "faces"; Name = "face_detection_yunet_2023mar.onnx";
       Url = "$zoo/face_detection_yunet/face_detection_yunet_2023mar.onnx";
       Sha256 = "8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4" },
    @{ Set = "faces"; Name = "face_recognition_sface_2021dec.onnx";
       Url = "$zoo/face_recognition_sface/face_recognition_sface_2021dec.onnx";
       Sha256 = "0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79" },
    @{ Set = "segment"; Name = "selfie_segmentation.onnx";
       Url = "$selfie/onnx/model.onnx";
       Sha256 = "3241ac4ad8aa35bdaf33946776db29f7c283a413aa0b0dacb9483594b4531aad" }
)

function Test-Model([string]$path, [string]$sha256) {
    (Test-Path -LiteralPath $path) -and ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $sha256)
}

# Windows PowerShell 5.1 does not offer TLS 1.2 by default.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

foreach ($model in $models) {
    $folder = Join-Path $Destination $model.Set
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $target = Join-Path $folder $model.Name
    if (Test-Model $target $model.Sha256) { continue }

    $cached = Join-Path (Join-Path $Cache $model.Set) $model.Name
    if (Test-Model $cached $model.Sha256) {
        Copy-Item -LiteralPath $cached -Destination $target -Force
        continue
    }

    Write-Host "Downloading $($model.Name)..."
    $partial = "$target.part"
    try {
        Invoke-WebRequest -Uri $model.Url -OutFile $partial -UseBasicParsing
    }
    catch {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        throw
    }
    if (-not (Test-Model $partial $model.Sha256)) {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        throw "$($model.Name): the download does not match its SHA-256"
    }
    Move-Item -LiteralPath $partial -Destination $target -Force
}
Write-Host "Hand, face and segmentation models are in $Destination"

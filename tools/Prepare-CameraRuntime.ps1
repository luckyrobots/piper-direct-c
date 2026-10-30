param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path $PSScriptRoot -Parent
$taskCache=Join-Path $taskRepo 'build/camera-deps'
New-Item -ItemType Directory -Path $taskCache,$Destination -Force | Out-Null
function Get-VerifiedFile([string]$Url,[string]$Path,[string]$Sha256) {
    if (!(Test-Path -LiteralPath $Path)) { Invoke-WebRequest -Uri $Url -OutFile $Path }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) { throw "Camera dependency hash mismatch: $Path" }
}
$taskSdk=Join-Path $taskCache 'RealSense.SDK-2.59.1.exe'
Get-VerifiedFile 'https://github.com/realsenseai/librealsense/releases/download/v2.59.1/RealSense.SDK-WIN10-2.59.1.11053.exe' $taskSdk '68502E782E3D906FB36B95ACACEB3F4F158DF9E22129A051286E4ACABF777A33'
$taskExtractorZip=Join-Path $taskCache 'innoextract-1.9-windows.zip'
Get-VerifiedFile 'https://github.com/dscharrer/innoextract/releases/download/1.9/innoextract-1.9-windows.zip' $taskExtractorZip '6989342C9B026A00A72A38F23B62A8E6A22CC5DE69805CF47D68AC2FEC993065'
$taskExtractorDirectory=Join-Path $taskCache 'innoextract'
Expand-Archive -LiteralPath $taskExtractorZip -DestinationPath $taskExtractorDirectory -Force
$taskNativeRoot=Join-Path $taskCache 'realsense-sdk'
$taskNative=Join-Path $taskNativeRoot 'app/bin/x64/realsense2.dll'
if (!(Test-Path -LiteralPath $taskNative)) {
    & (Join-Path $taskExtractorDirectory 'innoextract.exe') --extract --include 'app\bin\x64\realsense2.dll' --output-dir $taskNativeRoot $taskSdk
    if($LASTEXITCODE){throw 'Could not extract the RealSense runtime'}
}
if ((Get-FileHash -LiteralPath $taskNative -Algorithm SHA256).Hash -ne 'A55FB2C1C947FD4D5C4E242E1B892127424FBEDEA1279E375EC2884D3E1FAD92') { throw 'Unexpected RealSense native library' }
Copy-Item -LiteralPath $taskNative -Destination $Destination -Force
# Extraction only: no SDK installer, system driver, service or PATH change.

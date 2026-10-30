param([ValidateSet('win-x64','linux-x64')][string]$Runtime='win-x64')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path $PSScriptRoot -Parent
$taskPackage=Join-Path $taskRepo ('build/engine-plugin/'+$Runtime+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskPackage -Force | Out-Null
dotnet publish (Join-Path $taskRepo 'managed/Piper.Plugin') -c Release -r $Runtime --self-contained true -o $taskPackage
if($LASTEXITCODE){throw 'Plugin publish failed'}
$taskCameraRuntime=Join-Path $taskPackage 'camera-runtime'
dotnet publish (Join-Path $taskRepo 'managed/Piper.Camera') -c Release -r $Runtime --self-contained true -o $taskCameraRuntime
if($LASTEXITCODE){throw 'Camera runtime publish failed'}
if($Runtime -eq 'win-x64') {
    & (Join-Path $PSScriptRoot 'Prepare-CameraRuntime.ps1') -Destination $taskCameraRuntime
    & (Join-Path $taskCameraRuntime 'LuckyRobots.Piper.Camera.exe') check
    if($LASTEXITCODE){throw 'Bundled camera runtime validation failed'}
} else {
    $taskRealSense=Join-Path $taskRepo 'build/linux-x64/librealsense2.so'
    if(!(Test-Path -LiteralPath $taskRealSense)){throw 'Build RealSense 2.59.1 for the Linux release target into build/linux-x64/librealsense2.so first; see docs/lucky-engine-plugin.md.'}
    Copy-Item -LiteralPath $taskRealSense -Destination $taskCameraRuntime
}
Copy-Item -LiteralPath (Join-Path $taskRepo 'vendor/camera-licenses') -Destination (Join-Path $taskCameraRuntime 'licenses') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskRepo 'vendor/realsense/LICENSE') -Destination (Join-Path $taskCameraRuntime 'licenses/RealSense-LICENSE.txt') -Force
dotnet publish (Join-Path $taskRepo 'managed/Piper.Host') -c Release -r $Runtime --self-contained true -o (Join-Path $taskPackage 'server')
if($LASTEXITCODE){throw 'Controller server publish failed'}
dotnet publish (Join-Path $taskRepo 'managed/Piper.Pika.Agent') -c Release -r linux-x64 --self-contained true -o (Join-Path $taskPackage 'server/pika-agent')
if($LASTEXITCODE){throw 'Pika Linux/WSL agent publish failed'}
if($Runtime -eq 'win-x64') {
    foreach($taskLibrary in @('piper-core.dll','piper-cando.dll')) {
        Copy-Item -LiteralPath (Join-Path $taskRepo ('build/windows-x64/'+$taskLibrary)) -Destination (Join-Path $taskPackage 'server') -Force
    }
} else {
    Copy-Item -LiteralPath (Join-Path $taskRepo 'build/libpiper-core.so') -Destination (Join-Path $taskPackage 'server') -Force
}
Copy-Item -LiteralPath (Join-Path $taskRepo 'lucky-plugin.yaml') -Destination $taskPackage -Force
Copy-Item -LiteralPath (Join-Path $taskRepo 'web') -Destination $taskPackage -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $taskPackage 'docs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRepo 'docs/lucky-engine-plugin.md') -Destination (Join-Path $taskPackage 'docs') -Force
Copy-Item -LiteralPath (Join-Path $taskRepo 'managed/THIRD-PARTY-NOTICES.txt') -Destination $taskPackage -Force
Copy-Item -LiteralPath (Join-Path $taskRepo 'vendor/candle') -Destination (Join-Path $taskPackage 'transport-source') -Recurse -Force
# Include reproducible plugin/controller sources, never local state, captures, credentials or engine code.
$taskSource=Join-Path $taskPackage 'source'
New-Item -ItemType Directory -Path $taskSource -Force | Out-Null
foreach($taskDirectory in @('src','web','vendor/candle')) {
    $taskDestination=Join-Path $taskSource $taskDirectory
    New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $taskRepo $taskDirectory) -File | Copy-Item -Destination $taskDestination -Force
}
foreach($taskFile in @('cJSON.c','cJSON.h')){Copy-Item -LiteralPath (Join-Path $taskRepo ('vendor/'+$taskFile)) -Destination (Join-Path $taskSource 'vendor') -Force}
foreach($taskProject in @('Piper.Contracts','Piper.Core','Piper.Host','Piper.Pika','Piper.Pika.Agent','Piper.Plugin','Piper.Plugin.Tests','Piper.Camera','Piper.Camera.Tests','Piper.RealSense')) {
    foreach($taskFile in Get-ChildItem -LiteralPath (Join-Path $taskRepo ('managed/'+$taskProject)) -File -Recurse | Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs','.csproj','.proto','.svg','.json')}) {
        $taskRelative=$taskFile.FullName.Substring($taskRepo.Length+1)
        $taskDestination=Join-Path $taskSource $taskRelative
        New-Item -ItemType Directory -Path (Split-Path $taskDestination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination -Force
    }
}
Copy-Item -LiteralPath (Join-Path $taskRepo 'vendor/realsense'),(Join-Path $taskRepo 'vendor/camera-licenses') -Destination (Join-Path $taskSource 'vendor') -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $taskSource 'tools'),(Join-Path $taskSource 'docs') -Force | Out-Null
foreach($taskFile in @('Build-EnginePlugin.ps1','Prepare-CameraRuntime.ps1','build-windows.ps1','build-native-linux.sh')){Copy-Item -LiteralPath (Join-Path $taskRepo ('tools/'+$taskFile)) -Destination (Join-Path $taskSource 'tools') -Force}
Copy-Item -LiteralPath (Join-Path $taskRepo 'lucky-plugin.yaml') -Destination $taskSource -Force
Copy-Item -LiteralPath (Join-Path $taskRepo 'Makefile') -Destination $taskSource -Force
Copy-Item -LiteralPath (Join-Path $taskRepo 'README.md') -Destination $taskSource -Force
Copy-Item -LiteralPath (Join-Path $taskRepo 'managed/README.md') -Destination (Join-Path $taskSource 'managed') -Force
Copy-Item -LiteralPath (Join-Path $taskRepo 'docs/lucky-engine-plugin.md') -Destination (Join-Path $taskSource 'docs') -Force
Copy-Item -LiteralPath (Join-Path $taskRepo 'managed/THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $taskSource 'managed') -Force
$taskArchive=Join-Path $taskRepo ('build/Pipers-0.1.0-preview.1-'+$Runtime+'.zip')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZipFile=[IO.File]::Open($taskArchive,[IO.FileMode]::Create)
try {
    $taskZip=New-Object IO.Compression.ZipArchive($taskZipFile,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        foreach($taskFile in Get-ChildItem -LiteralPath $taskPackage -File -Recurse | Sort-Object FullName) {
            $taskRelative=$taskFile.FullName.Substring($taskPackage.Length+1).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip,$taskFile.FullName,$taskRelative) | Out-Null
        }
    } finally { $taskZip.Dispose() }
} finally { $taskZipFile.Dispose() }
Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256 | Select-Object Path,Hash

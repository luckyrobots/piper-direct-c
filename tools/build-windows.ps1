param(
    [string]$Compiler = 'gcc',
    # Copy the native DLLs into this directory (for example a published host) when they differ.
    [string]$StageTo = '',
    # Stage the already-built DLLs without building.
    [switch]$StageOnly
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskOut = Join-Path $taskRoot 'build/windows-x64'
New-Item -ItemType Directory -Force $taskOut | Out-Null
Push-Location $taskRoot
# Copy only changed DLLs, so a running host's files are replaced only when rebuilt.
function Copy-NativeLibraries {
    if (!$StageTo) { return }
    foreach ($taskLibrary in @('piper-core.dll', 'piper-cando.dll')) {
        $taskBuilt = Join-Path $taskOut $taskLibrary
        $taskStaged = Join-Path $StageTo $taskLibrary
        if (!(Test-Path -LiteralPath $taskBuilt)) { continue }
        if ((Test-Path -LiteralPath $taskStaged) -and (Get-FileHash -LiteralPath $taskBuilt).Hash -eq (Get-FileHash -LiteralPath $taskStaged).Hash) { continue }
        Copy-Item -LiteralPath $taskBuilt -Destination $taskStaged -Force
    }
}
try {
    if ($StageOnly) { Copy-NativeLibraries; return }
    & $Compiler -shared -static-libgcc -std=c17 -O2 -g -DCANDLE_API_LIBRARY -DUNICODE -D_UNICODE -Ivendor/candle -o "$taskOut/piper-cando.dll" vendor/candle/candle.c vendor/candle/candle_ctrl_req.c vendor/candle/piper_adapter.c -lsetupapi -lwinusb -lole32
    if ($LASTEXITCODE -ne 0) { throw 'Windows USB transport build failed' }
    & $Compiler -shared -static-libgcc -std=c17 -O2 -g -Wall -Wextra -Werror -Isrc -Ivendor -o "$taskOut/piper-core.dll" src/managed_bridge.c src/controller.c src/canbus.c src/protocol.c vendor/cJSON.c -lm -lsetupapi -lcfgmgr32
    if ($LASTEXITCODE -ne 0) { throw 'Native core build failed' }
    dotnet build managed/Piper.Host/Piper.Host.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '.NET host build failed' }
    Copy-NativeLibraries
} finally { Pop-Location }

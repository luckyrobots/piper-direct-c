param([string]$Compiler = 'gcc')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskOut = Join-Path $taskRoot 'build/windows-x64'
New-Item -ItemType Directory -Force $taskOut | Out-Null
Push-Location $taskRoot
try {
    & $Compiler -shared -static-libgcc -std=c17 -O2 -g -DCANDLE_API_LIBRARY -DUNICODE -D_UNICODE -Ivendor/candle -o "$taskOut/piper-cando.dll" vendor/candle/candle.c vendor/candle/candle_ctrl_req.c vendor/candle/piper_adapter.c -lsetupapi -lwinusb -lole32
    if ($LASTEXITCODE -ne 0) { throw 'Windows USB transport build failed' }
    & $Compiler -shared -static-libgcc -std=c17 -O2 -g -Wall -Wextra -Werror -Isrc -Ivendor -o "$taskOut/piper-core.dll" src/managed_bridge.c src/controller.c src/canbus.c src/protocol.c vendor/cJSON.c -lm -lsetupapi -lcfgmgr32
    if ($LASTEXITCODE -ne 0) { throw 'Native core build failed' }
    dotnet build managed/Piper.Host/Piper.Host.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '.NET host build failed' }
} finally { Pop-Location }

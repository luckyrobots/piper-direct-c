param([string]$Compiler='gcc', [string]$Make='make')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location $taskRepo
try {
    # Every fixture replaces USB/process boundaries with callbacks. None
    # enumerates or opens an adapter, starts a server, or issues a robot command.
    New-Item -ItemType Directory -Force build | Out-Null
    # These three Windows-only fixtures have no Makefile rule yet. The pump
    # fixture still exercises the streamed-position batch until that
    # experimental transport and its fixture cases are deleted.
    & $Compiler -std=c17 -O2 -g -DCANDLE_API_LIBRARY -DUNICODE -D_UNICODE -Ivendor/candle tests/test_candle_tx_pump_windows.c vendor/candle/candle_ctrl_req.c -o build/test-candle-tx-pump-windows.exe -lsetupapi -lwinusb -lole32
    if ($LASTEXITCODE) { throw 'Fake WinUSB fixture did not compile.' }
    & $Compiler -std=c17 -O2 -g -Wall -Wextra -Werror -DPIPER_ENABLE_EXPERIMENTAL_STREAMED_POSITION=1 -Isrc -Ivendor tests/test_can_windows_pump.c src/protocol.c -o build/test-can-windows-pump.exe -lm -lsetupapi -lcfgmgr32
    if ($LASTEXITCODE) { throw 'Fake native receive-pump fixture did not compile.' }
    & $Compiler -std=c17 -O2 -g -Wall -Wextra -Werror -Isrc -Ivendor tests/test_can_windows_hotplug.c src/controller.c src/protocol.c vendor/cJSON.c -o build/test-can-windows-hotplug.exe -lm -lsetupapi -lcfgmgr32
    if ($LASTEXITCODE) { throw 'Offline Windows hotplug fixture did not compile.' }
    # The shared offline fixtures use the Makefile rules with the Windows libraries.
    $taskShared='test-idle-reconnect','test-multi-lanes','test-pose-entry-order','test-disable-sequence'
    & $Make "CC=$Compiler" 'LDLIBS=-lm -lsetupapi -lcfgmgr32' @($taskShared | ForEach-Object { "build/$_" })
    if ($LASTEXITCODE) { throw 'Offline Windows fixtures did not compile.' }
    foreach ($taskTest in @('test-candle-tx-pump-windows','test-can-windows-pump','test-can-windows-hotplug') + $taskShared) {
        & "./build/$taskTest.exe"
        if ($LASTEXITCODE) { throw "$taskTest failed." }
    }
} finally { Pop-Location }

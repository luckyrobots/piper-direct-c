param([string]$Compiler='gcc')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path $PSScriptRoot -Parent
Push-Location $taskRepo
try {
    # Both fixtures replace USB/process boundaries with callbacks. They never
    # enumerate or open an adapter, start a server, or issue a robot command.
    & $Compiler -std=c17 -O2 -g -DCANDLE_API_LIBRARY -DUNICODE -D_UNICODE -Ivendor/candle tests/test_candle_tx_pump_windows.c vendor/candle/candle_ctrl_req.c -o build/test-candle-tx-pump-windows.exe -lsetupapi -lwinusb -lole32
    if ($LASTEXITCODE) { throw 'Fake WinUSB fixture did not compile.' }
    & ./build/test-candle-tx-pump-windows.exe
    if ($LASTEXITCODE) { throw 'Fake WinUSB fixture failed.' }
    # Lab-only protocol opt-in for fake callbacks. Production Build-Windows.ps1
    # never enables this switch; hardware qualification has not passed.
    & $Compiler -std=c17 -O2 -g -Wall -Wextra -Werror -DPIPER_ENABLE_EXPERIMENTAL_STREAMED_POSITION=1 -Isrc -Ivendor tests/test_can_windows_pump.c src/protocol.c -o build/test-can-windows-pump.exe -lm -lsetupapi -lcfgmgr32
    if ($LASTEXITCODE) { throw 'Fake native receive-pump fixture did not compile.' }
    & ./build/test-can-windows-pump.exe
    if ($LASTEXITCODE) { throw 'Fake native receive-pump fixture failed.' }
    & $Compiler -std=c17 -O2 -g -Wall -Wextra -Werror -Isrc -Ivendor tests/test_can_windows_hotplug.c src/controller.c src/protocol.c vendor/cJSON.c -o build/test-can-windows-hotplug.exe -lm -lsetupapi -lcfgmgr32
    if ($LASTEXITCODE) { throw 'Offline Windows hotplug fixture did not compile.' }
    & ./build/test-can-windows-hotplug.exe
    if ($LASTEXITCODE) { throw 'Offline Windows hotplug fixture failed.' }
    & $Compiler -std=c17 -O2 -g -Wall -Wextra -Werror -Isrc -Ivendor tests/test_idle_reconnect.c src/controller.c src/canbus.c src/protocol.c vendor/cJSON.c '-Wl,--wrap=piper_now' '-Wl,--wrap=can_bus_receive' '-Wl,--wrap=can_bus_reconnect' '-Wl,--wrap=can_bus_reconnect_device' '-Wl,--wrap=can_bus_send' -o build/test-idle-reconnect.exe -lm -lsetupapi -lcfgmgr32
    if ($LASTEXITCODE) { throw 'Offline per-device idle reconnect fixture did not compile.' }
    & ./build/test-idle-reconnect.exe
    if ($LASTEXITCODE) { throw 'Offline per-device idle reconnect fixture failed.' }
    & $Compiler -std=c17 -O2 -g -Wall -Wextra -Werror -Isrc -Ivendor tests/test_multi_lanes.c src/controller.c src/canbus.c src/protocol.c vendor/cJSON.c '-Wl,--wrap=piper_now' '-Wl,--wrap=can_bus_receive' '-Wl,--wrap=can_bus_send' -o build/test-multi-lanes.exe -lm -lsetupapi -lcfgmgr32
    if ($LASTEXITCODE) { throw 'Offline multi-device motion-lane fixture did not compile.' }
    & ./build/test-multi-lanes.exe
    if ($LASTEXITCODE) { throw 'Offline multi-device motion-lane fixture failed.' }
    & $Compiler -std=c17 -O2 -g -Wall -Wextra -Werror -Isrc -Ivendor tests/test_pose_entry_order.c src/controller.c src/canbus.c src/protocol.c vendor/cJSON.c '-Wl,--wrap=piper_now' '-Wl,--wrap=can_bus_receive' '-Wl,--wrap=can_bus_send' -o build/test-pose-entry-order.exe -lm -lsetupapi -lcfgmgr32
    if ($LASTEXITCODE) { throw 'Offline position-entry ordering fixture did not compile.' }
    & ./build/test-pose-entry-order.exe
    if ($LASTEXITCODE) { throw 'Offline position-entry ordering fixture failed.' }
    & $Compiler -std=c17 -O2 -g -Wall -Wextra -Werror -Isrc -Ivendor tests/test_disable_sequence.c src/controller.c src/canbus.c src/protocol.c vendor/cJSON.c '-Wl,--wrap=piper_now' '-Wl,--wrap=can_bus_receive' '-Wl,--wrap=can_bus_send' -o build/test-disable-sequence.exe -lm -lsetupapi -lcfgmgr32
    if ($LASTEXITCODE) { throw 'Offline staged-disable fixture did not compile.' }
    & ./build/test-disable-sequence.exe
    if ($LASTEXITCODE) { throw 'Offline staged-disable fixture failed.' }
} finally { Pop-Location }

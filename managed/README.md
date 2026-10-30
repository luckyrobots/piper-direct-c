# C# server, Pika transport and camera runtime

`Piper.Host` is the shared .NET 9 HTTP/gRPC server for the native Lucky Engine
plugin and web dashboard. `Piper.Core` serializes access to the existing C17
motion/protocol core; the reusable core has no ASP.NET dependency. The C core
retains feedback, ownership, limits, obstruction detection and recovery records.

Projects:

| Project | Responsibility |
| --- | --- |
| Piper.Core / Piper.Host | Native controller ABI, state loop and local HTTP/gRPC host |
| Piper.Contracts | Public controller protobuf contracts |
| Piper.Pika / Piper.Pika.Agent | Pika protocol, ownership and Linux serial worker |
| Piper.Plugin | Native Lucky Engine widgets and public scene API integration |
| Piper.Camera / Piper.RealSense | Isolated C# OpenCV capture/calibration and official RealSense wrapper |

## Windows build and dashboard

Install the .NET 9 SDK and x64 MinGW GCC for development. End-user release
packages are self-contained and do not require the SDK.

```powershell
./tools/build-windows.ps1 -Compiler C:/path/to/gcc.exe
dotnet publish managed/Piper.Host -c Release -r win-x64 --self-contained true -o build/publish-windows
./build/publish-windows/Piper.Host.exe --simulate --port 8765 --state-dir data-managed-sim --web-root web
```

For hardware, omit `--simulate`, stop other arm controllers and select the
intended adapter. The source-built `piper-cando.dll` must remain beside
`piper-core.dll`; the host loads the native core from its own output directory.
The Windows USB-CAN interface must use WinUSB with a compatible candleLight/
gs_usb firmware. Transport startup configures classical CAN at 1 Mbit/s and
monitors feedback without enabling, moving or resetting the robot.

The adapter's physical USB parent serial and arrival timestamp identify the
connection. A reconnect receives a new generation and invalidates measurements.
The transport bounds WinUSB writes and cancels pending reads before releasing
buffers. LGPL transport source and license notices accompany Windows packages.

Preserve `--state-dir` across updates. Hardware and simulator directories cannot
be interchanged. The authenticated `POST /api/shutdown` closes an idle host and
its USB handles cleanly before another controller opens the adapter.

## Pika on Windows through WSL

Native Windows WCH serial control is intentionally unavailable in this integration.
Pika uses Linux's serial driver through WSL; the rest of the server and the arm's
USB-CAN transport stay native Windows. This requires WSL and Microsoft's
[usbipd-win USB support](https://learn.microsoft.com/windows/wsl/connect-usb).
Installing a plugin does not replace kernel drivers or grant USB permissions.

Build the self-contained Linux agent:

```powershell
dotnet publish managed/Piper.Pika.Agent -c Release -r linux-x64 --self-contained true -o build/pika-agent-linux
```

For a manually published Windows host, copy that output beside the host as
`build/publish-windows/pika-agent/`. Plugin release packages already include it.
The host resolves the packaged executable through WSL and sets its Linux execute
permission; no root-owned `/opt` installation is required. An existing Linux
agent can alternatively be selected with `--pika-agent /absolute/linux/path`.
The WSL user needs access to the verified serial device, preferably its stable
`/dev/serial/by-id/` path.

Identify Pika by VID `1a86` / PID `7522`. In an administrator terminal, use
`usbipd bind --force --busid <verified-Pika-busid>`, then attach only that device
with `usbipd attach --wsl --busid <verified-Pika-busid>`. Review Microsoft's USB
sharing/firewall guidance and restrict sharing to the local WSL connection.
Never substitute the arm's USB-CAN adapter bus ID.

```powershell
./build/publish-windows/Piper.Host.exe --web-root web --state-dir data-windows-managed --pika-wsl Ubuntu --pika-device /dev/serial/by-id/YOUR_PIKA_DEVICE
```

`tools/Start-WindowsDashboard.ps1` supervises an already configured installation,
keeps its WSL distribution running and reattaches the already proxy-bound Pika
device. Pass `-Distribution` and `-PikaDevice` for the local installation. It
does not bind drivers, restore movement or enable motors. Each control session
requires fresh telemetry and a current owner. Foreground web tabs can explicitly
request ownership transfer; stale owners cannot issue new commands.

Pika opens at 460800 baud for telemetry. Only the current owner can control it.
Loss of heartbeat, stale feedback or a failed movement deadline requests disable;
closing an agent input also requests disable if that session enabled the motor.
These are software requests and depend on the transport remaining available.
Displayed opening is derived from the vendor linkage model and encoder zero,
not an independent jaw measurement or force calibration.

## Linux

```sh
bash tools/build-native-linux.sh
dotnet publish managed/Piper.Host -c Release -r linux-x64 --self-contained true -o build/publish-linux
./build/publish-linux/Piper.Host --simulate --state-dir data-managed-sim --web-root web
```

For real hardware, omit `--simulate` and configure the intended SocketCAN link
UP at 1 Mbit/s first. The Pika agent can run locally with the user's Linux tty
permissions. The original C CLI/server remains available through `make`.

## Lucky Engine and cameras

See the [Pipers plugin guide](../docs/lucky-engine-plugin.md) for the native panel,
public SDK, linking, packaging and calibration. Third-party plugin authors do
not need engine source or private engine assemblies.

The Windows plugin includes a self-contained C# camera worker, OpenCV and the
RealSense runtime. No Python, FFmpeg executable, .NET runtime installer or
RealSense SDK installation is needed for its cameras. The worker has no
robot-control references. Lumina/bench and Pika fisheye use DirectShow; D405
uses the official RealSense C# wrapper and factory stream intrinsics. The
optional web dashboard uses browser-local video previews; those are not metric
depth streams.

Profiles and camera selections persist outside the package directory. A
checkerboard workflow fits the fisheye lens and validates held-out images.
Lens calibration does not verify camera mounts or exposure synchronization;
metric depth previews do not reproduce stereo noise/holes. Keep these limits
visible when collecting data or comparing real and simulated views.

## Connection recovery and control state

Idle transport reopening uses backoff and re-queries missing firmware/limits.
Static configuration remains valid for an uninterrupted connection; live
feedback still expires. Reconnection never enables motors, clears a controller
stop or replays a target. HTTP/gRPC stay responsive to USB write timeouts.

Some adapter/firmware/driver stalls can outlive a process reopen. This public
package does not install a privileged USB-port reset service or flash adapter
firmware. Resolve those through the adapter's supported driver/firmware
procedure. Machine-specific recovery tools and firmware backups are not part
of the plugin distribution.

## Offline validation

```powershell
dotnet run --project managed/Piper.Pika.Tests -c Release
dotnet run --project managed/Piper.Plugin.Tests -c Release
dotnet run --project managed/Piper.Camera.Tests -c Release
```

The plugin tests resolve the public `LuckyEngine.PluginSdk` NuGet package. Obtain
its release artifact from
[luckyengine-plugins](https://github.com/luckyrobots/luckyengine-plugins/releases/tag/sdk-v0.1.0)
and add the download folder as a NuGet source, or pack it from that public repo.
No Lucky Engine checkout is required.

For simulator/API integration, build the host and set `PIPER_TEST_EXE` to its
absolute executable path before running `node tests/test_api.mjs`. The optional
gRPC integration fixture accepts only `http://127.0.0.1:18766`; start a separate
simulated host on that endpoint. Native `make test` runs on Linux. These suites
use simulated state/offline images and issue no physical robot commands.

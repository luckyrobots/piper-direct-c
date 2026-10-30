# PiPER Direct C and Pipers for Lucky Engine

A local controller and compact dashboard for one selected PiPER arm, its Pika
gripper and cameras. The native plugin stores up to eight robot profiles, with
explicit CAN assignments, per-robot wrist cameras and a shared bench camera.
The Windows and Linux C# host exposes the same HTTP API and
web UI. The native Lucky Engine plugin adds joint controls, real/simulated camera
previews and explicit scene-to-hardware linking through the public plugin SDK.

The shared C17 core owns CAN communication, motion scheduling, feedback checks,
limits and recovery records. C# owns the local server, gRPC integration and Pika
transport. The plugin does not require Lucky Engine source access.

## Choose an entry point

- **Lucky Engine:** install Pipers using a compatible API 1 editor. See the
  [plugin guide](docs/lucky-engine-plugin.md) for installation, linking, cameras,
  calibration and self-contained package builds.
- **Windows dashboard:** build and run the [.NET host](managed/README.md), using
  native candleLight/gs_usb USB-CAN and the Linux Pika agent through WSL.
- **Linux dashboard or CLI:** build the C controller with `make`, or use the same
  [.NET host](managed/README.md) over SocketCAN.

Windows plugin packages include the C# camera worker, .NET runtime, OpenCV and
RealSense native libraries. Users do not install Python, FFmpeg or the RealSense
SDK for plugin camera capture or fisheye lens calibration. Linux release builds
must provide native libraries for their target distribution; see the plugin guide.

## Linux native quick start

Requires a C17 compiler, Linux SocketCAN headers, make and libc/libm.

```sh
make
./build/piper-control --simulate --port 8877 --state-dir data-sim --web-root web
# Open http://127.0.0.1:8877/
```

For hardware, stop other software controlling the arm, configure the intended
SocketCAN link UP at 1 Mbit/s, and omit `--simulate`. Startup discovers adapters
and requests firmware/limits after fresh feedback; it does not enable motors or
move the arm. Keep hardware and simulator state directories separate.

```sh
./build/piper-control --port 8765 --state-dir data --web-root web
```

The `deploy/` files are examples from an earlier Linux installation. Review and
replace their service paths and adapter allowlist before installing them. They
are not an automatic setup for another machine.

## Controls and recovery

The shared web dashboard has one measured row for Pika and each of J1–J6, with
absolute sliders, minus/plus jog and zero controls. Camera selections, speed,
jog step and tolerance persist. Speed is 0–100% of the slowest fresh reported
joint speed limit, with the resulting degrees/second shown beside it. Zero
requests an angle of zero; it never writes encoder calibration.

Enable robot uses one workspace-clear prompt. It checks fresh controller and
motor feedback before powering joints, and the connected Pika follows the
robot's power state. Other joints retain measured targets during a single-joint
move. Tiny resting overshoot permits commands back into range; larger excursions
require the displayed bounded range-entry procedure.

[Obstruction detection and recovery](docs/obstruction-recovery.md) records why a
move stopped and the next available recovery step. Joint movements request STOP
after about 500 ms without measured progress when their command leads the joint
outside tolerance. This is position-feedback stall detection, not a force sensor
or collision planner. Controller stops require explicit recovery; reset can
remove holding torque. Recovery does not replay the interrupted move or rewrite
calibration.

The selected adapter ID and connection generation bind every movement. Hotplug,
stale feedback, controller faults, foreign control traffic and lost command
leases invalidate control. Reconnection reads information again and does not
resume movement or enable motors. STOP is a software request; delivery depends
on a working connection. Keep the physical stop available and support the arm
before changes that can remove holding torque.

Static firmware/limit configuration remains valid for one uninterrupted
connection. Reconnects and status-stream interruption invalidate it. Live joint,
controller and motor readings remain subject to their freshness checks. Use
Read firmware & limits after changing configuration in another application.

## Architecture

```text
Lucky Engine native panel (public PluginSdk)     Local web dashboard
            | loopback gRPC                           | HTTP
            +--------------------+---------------------+
                                 v
                         C# Piper.Host / Core
                       +---------+----------+
                       |                    |
                Native C motion core    C# Pika agent
                       |                    |
             WinUSB or SocketCAN       Linux tty / WSL
                       |                    |
                     PiPER                 Pika

C# camera worker + OpenCV/RealSense -> latest JPEG + factory/lens profile
                                      -> native real/sim camera previews
```

The native Linux C daemon remains an alternative server using the same C core.
Only one controller should own an arm at a time. Native SDK/transport code and
license notices are included; robot firmware flashing is outside this workflow.

## API

The [OpenAPI specification](web/openapi.json) is served at `/api/openapi.json`.
The C# host additionally exposes loopback HTTP/2 gRPC on port 8766; its shared
schema is `managed/Piper.Contracts/piper.proto`.

- `GET /api/state`: identity, generation, measurements, limits, connection and incident state.
- `POST /api/select`, `/api/query`, `/api/reconnect`: discovery and read-only configuration.
- `POST /api/command`: implemented joint, pose, enable/disable, hold and recovery actions.
- `POST /api/heartbeat`: renew one owner/request-ID command lease.
- `POST /api/stop`: best-effort software stop and durable incident record.

Every POST requires JSON and `X-Piper-Token` from state. Commands bind to the
selected `device_id`, current `generation` and a unique `owner`. HTTP 202 means
accepted, not reached; inspect subsequent feedback. Do not replay an actuator
request after an ambiguous response. The API does not expose arbitrary CAN,
encoder-zero writes, MIT torque/gain control or a firmware flasher.

## Passive recording and diagnostics

`build/piper-record` records measured observations without sending robot CAN
commands. `build/piper-trace` records bus traffic and local/remote origin. They
do not establish synchronized camera exposures or download onboard recordings.

```sh
./build/piper-record --device usb:YOUR_ADAPTER_SERIAL --seconds 60 --out demonstration.jsonl
./build/piper-trace --device usb:YOUR_ADAPTER_SERIAL --seconds 60 --out trace.jsonl
./build/piper-can --scan --seconds 5
```

The optional `tools/capture_episode.py` packages passive recordings using
`pyarrow`; that developer/export tool is independent of the bundled plugin's
C# camera runtime. Existing output paths are rejected and incomplete captures
are labeled as incomplete.

## Validation

```sh
make test
# Optional privileged virtual-CAN and passive-recorder checks:
make test-vcan
make test-record
```

Managed/plugin/camera fixtures and Windows/Linux build instructions are in the
[managed guide](managed/README.md). CI uses simulators and offline fixtures;
physical camera latency, calibration and robot behavior require separate live
verification. Cameras use host receipt timestamps until exposure-clock alignment
is implemented.

Protocol references: [official AgileX message definitions](https://github.com/agilexrobotics/piper_sdk/tree/c9e8a28174e71eeaac448593cb65f8ab258a92fe/piper_sdk/piper_msgs/msg_v2),
[Linux SocketCAN](https://docs.kernel.org/networking/can.html),
[public Lucky Engine plugin SDK](https://github.com/luckyrobots/luckyengine-plugins).

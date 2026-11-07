# Pipers for Lucky Engine

Pipers adds a native `Plugin:Pipers` workspace: scene hierarchy, robot controls,
and the normal engine viewport. It uses the public LuckyEngine.PluginSdk package;
building it does not require Lucky Engine source, native headers or Hazel assemblies.

This is an API 1 development preview. The arm's existing controller remains the
single owner of USB-CAN and the Pika connection. The plugin communicates with it
over loopback gRPC (8766), alongside the existing web dashboard (8765).

## Controls

- Six measured joint rows, absolute targets, minus/plus jog, individual zero and all-zero.
- Robot-reported angle and speed limits; speed 0–100%, degrees/second, jog and tolerance.
- Pika opening in millimetres; Enable robot enables Pika through the same server.
- Bench/Lumina, Pika gripper and D405 preview tiles with preserved aspect ratio.
- Camera, speed, jog and tolerance settings retained between editor sessions.
- Hand-guided teaching detection, a firmware-aware measured-pose standby/CAN return,
  and a single-page per-robot desk calibration sheet. See
  [teaching and alignment](teaching-alignment.md).
- Explicit recovery controls for controller stops; existing backend obstruction
  detection and motion leases remain authoritative.

## Follow simulation (development preview)

`Follow simulation` is an explicit, session-only mode. It is off after every
restart, disconnect, scene change or stop. It uses the public plugin runtime
API; plugin authors still need only the published SDK, not engine source.

1. In Edit mode, select the physical CAN adapter and its scene robot, then enable
   the arm and assigned Pika controls. End onboard teaching/playback first.
2. Check that the path to the simulation's start pose is clear, then click
   **Follow simulation**. This prepares and shows the actual first frame, paused
   before any physics/script update. Play stays locked during preparation and
   matching.
3. The real arm and Pika automatically match that starting pose at the slow
   matching speed. The panel shows the measured and requested positions and
   matching progress. **Stop following** cancels this movement and holds the
   measured pose.
4. When the positions agree, the panel says **Ready — press Play**. The simulation
   stays at its first frame, and neither hardware motion stream is running while
   you wait. The motors keep holding the matched pose.
5. Press Play. The editor records your request but keeps that first frame paused
   while the plugin starts both hardware sessions and confirms their initial
   targets. Only the acknowledged, exactly scoped activation releases Play.
6. During Play, the solid robot is the simulation leader at the scene's selected
   rate. A cyan mesh at 50% opacity shows fresh measured hardware joints and
   jaws. Use **Free Camera** in the Scene viewport to see the overlay during
   Play. The ghost never changes physics, collisions, camera images or datasets.
7. The real follower buffers one closed movement phase, preserving every source
   waypoint and the order of jaw events. Continuous monotone cubic arm paths
   use local timing qualified against fresh reported velocity/acceleration
   limits. The leader can run ahead; that lag alone does not stop it. The speed
   selected at preparation stays pinned until Stop following.
8. A completed simulation episode displays **Waiting for real robot**. Its next
   randomization waits for fresh stationary arm feedback, the expected measured
   jaw opening, and acknowledgement of that exact final source frame. The
   existing tube task uses this reusable scene-script episode gate.

**Stop following** holds measured arm/Pika positions and pauses the simulation.
The separate **STOP** action remains immediate. Changed identity, stale input,
unknown actuator law, out-of-range targets, a fault or excessive divergence
disarm the session; previous targets do not resume. CAN references use reported
joint speed/acceleration limits and a replaceable 250 ms input deadline.

The CAN reference shaper brakes before both reported joint boundaries and a
**2° measured-position lead boundary**. That is soft scheduling headroom; the
independent **3° native tracking fault** remains unchanged. Braking includes the
millidegree wire grid and reported acceleration, so a moving destination cannot
accelerate the reference into its lead boundary and leave no valid next step.
If a fresh encoder update nevertheless makes the software boundary infeasible,
the controller retires the trajectory and verifies a measured-pose hold, with no
automatic replay. Hardware faults, changed/invalid limits, invalid arithmetic,
excessive tracking error and an existing obstruction still use their hard stop;
failure to verify or transmit the hold also stops the controller.

The public runtime API must advertise `startActivationSupported` and the ordered
`robotRuntimeFramesSupported` capability. The plugin
opts into `prepare-start` with `activationHandshake: true`, observes
`startRequested`, and uses `activate-start` only after controller admission.
An older editor is rejected before physical matching. Legacy SDK clients that
do not opt into this handshake retain the original preparation semantics.

An inactive Ready state waits through brief feedback-age gaps without starting
either hardware stream. Identity, ownership, fault and changed-pose checks still
apply. Once controller admission begins, the active freshness deadlines apply.

Ordered frames are captured at the selected robot runner's Export phase, rather
than reconstructed from RPC poll intervals. Scene event and post-physics MuJoCo
clocks are distinct. Every source cursor is retained; a gap, an unacknowledged
episode reset, or buffer overflow fails closed. The bounded queue pauses only
its source at high water and resumes after draining; it never skips a grip or
release. Fresh source envelopes and hardware feedback still have strict
deadlines. The native command shaper remains the final hardware rate bound.

Arm and jaw phases must be separate for this preview. Jaw endpoints are checked
against the simulation's measured contact opening, not blindly against a
command narrower than a held object. Pika exposes no verified physical opening
rate, so simultaneous moving-arm/jaw phases are rejected. An opening match does
not independently establish a secure grasp.

A missed native reference tick still discards delayed targets. If fresh CAN
feedback and a measured hold can be verified, the controller retires following
while keeping the motors enabled. A failed hold or a genuine controller fault
still requests STOP. The first failure and subsequent cleanup results are saved
under the plugin settings directory at `diagnostics/simulation-follow`.

This is position-reference following, not proof of identical contact dynamics,
pixel alignment or zero latency. Place physical objects at the episode's saved
start coordinates before a physical pick/place attempt. The generic follow
button does not detect or relocate real objects. Simulation recording time is
distinct from wall time when deterministic rendering cannot run in real time.

### Firmware-dependent speed units

Maximum-speed feedback (CAN `0x473`) uses 0.001 rad/s per raw unit before firmware
1.5-8 and 0.01 rad/s from 1.5-8 onward. Raw 300 on 1.9-1 therefore means 3 rad/s,
not 0.3. Raw bytes, resolved units and firmware provenance are exposed beside
the interpreted limits. Incomplete/unknown firmware leaves the unit unresolved;
it never gains movement authority by assuming a version. See the
[official speed message definition](https://github.com/agilexrobotics/piper_sdk/blob/master/piper_sdk/piper_msgs/msg_v2/feedback/arm_feedback_current_motor_angle_limit_max_spd.py).

Maximum-acceleration feedback (`0x47C`) uses **0.01 rad/s² per unsigned raw unit**:
raw500 means5 rad/s². This follows the current official parser and its matching
write/readback conversion, rather than an older feedback-class comment. The
controller still rejects unavailable, nonpositive or above5 rad/s² reports;
this correction does not write controller settings or change admission guards.
See the [pinned acceleration-unit evidence](protocol-acceleration-units.md).

## Native workspace

Controls, Calibration, Recording and Settings are separate views. The padded
robot sidebar keeps the shared bench preview visible. CAN assignment, serial
device paths, controller setup and camera stream/lens tools live in Settings;
ordinary operation shows compact measured rows and contextual tooltips.
Enlarge a camera to compare its real and simulation counterparts. Background
capture and telemetry continue independently of page navigation.

Each rendered view has an event epoch as well as its robot profile identity.
Leaving Controls ends this session's active viewport gesture/target and discards
its control permission; returning never resumes old gestures. STOP and hold
remain tied to the same robot across page changes. One explicit workspace-clear
prompt precedes enabling controls. Reset/recovery support requirements remain
visible when they apply.

Calibration shows all four reference points and the independent Check together.
Enter BACK/LEFT measurements in millimetres; edits save as you type and restore
next session. Confirm the desk edges, model jaw-tip point and tabletop, then
**Select P1**, physically hand-guide the jaw-tip midpoint onto its cross, hold
still and **Capture P1**. Repeat P2–P4 and Check. Selection only frames the
viewport. The page shows captured counts and inline blockers, including duplicate
marks and invalid measurement accuracy (1–10 mm; **Use ±5 mm** is available).
Passive preview actions preserve usable controls rather than rebuilding the
sheet. Fresh-pose guards and scene-only Apply/Undo remain authoritative.
See [the calibration guide](robot-base-calibration.md). JPEG previews help with
framing and contact; they are not a metric depth query or proof of camera alignment.
The Recording view reports setup requirements; it does not claim synchronized
data or start the engine recorder before those requirements are verified.

## Bounded range entry

A small measured range overrun is distinct from a driver fault. The offending
joint's normal inward control remains available when it is the only significant
overrun and the full requested pose is legal. `Move all to zero` also accepts an
explicit bounded correction (at most 3 degrees), enters the valid range at 1%
controller speed, verifies measured settling, then proceeds to zero under the
same lease. It changes position, never encoder calibration.

Every CAN position command includes all six joints. A different joint therefore
remains blocked while another joint prevents a legal six-joint target; its tooltip
identifies that joint and the available inward/all-zero action. Multiple overruns
require the explicit all-zero route. Faults, stale feedback, excessive correction,
lost leases and obstruction stops retain their existing protections. Cancelling,
teaching takeover or reconnecting discards the pending destination. Range entry
and homing never start automatically from refresh, reconnection or calibration.

## Public SDK dependency

The source uses the public MIT `LuckyEngine.PluginSdk` 0.1.1 preview package,
pinned under `vendor/plugin-sdk` with its SHA-256 and provenance. Relative
`NuGet.Config` and bundled source include that feed, so independent authors need
no engine checkout or private assemblies. Installed packages include the SDK,
.NET and camera libraries; they do not invoke NuGet or require Python.

## Multiple robots

The left sidebar stores up to eight named robot profiles and one shared
Bench/Lumina camera. Select a robot to see its existing joint/Pika controls and
its two wrist camera previews beside the viewport. The shared bench capture
keeps running when profiles change. Only the selected robot's wrist captures
run; new profiles start with cameras and hardware assignments unassigned.

Assign each robot's **CAN adapter** explicitly using the adapter's stable device
identity. Offline assignments remain visible and never fall back to the first
connected adapter. Profiles retain separate scene-robot UUIDs, speed, jog,
tolerance, wrist camera paths, D405 serials, simulation camera counterparts and
lens calibration. One active robot is controlled at a time: the backend currently
has one selected CAN/motion context. Switching holds only this session's active
movement, releases its Pika connection, clears Enable approval and unlinks the
previous scene robot. It never enables motors, repeats a move or links another
robot automatically. A foreign active movement blocks switching. If another
client changes the server's selected CAN adapter, this profile becomes read-only
until **Activate assigned CAN** selects the correct adapter again.

Set each robot's **Pika serial device** separately; blank means no gripper is
assigned. On Windows, also choose its WSL distribution. Prefer a unique
`/dev/serial/by-id/...` identity; if identical WCH adapters have no unique serial
and their aliases collide, use `/dev/serial/by-path/...` for a fixed physical USB
port. Map the physical gripper to its CAN robot deliberately. Do not select a
generic first device or share one path between profiles. Camera names can also
be ambiguous: use unique DirectShow device paths on Windows and stable
`/dev/v4l/by-id/...` or `/dev/v4l/by-path/...` paths on Linux, plus an explicit
D405 serial whenever multiple depth cameras are connected.

The first legacy profile inherits the previous settings and the currently
present, selected adapter. Other profiles require explicit assignment. Pika
routing changes use owner-checked `/api/pika/configure` while arm motion is idle;
configuration itself sends no motor commands and opens no serial device. An
owned Pika session must first produce disabled feedback and disconnect. A failed
agent does not prove its motor is off: reconnect that same device and verify
disabled feedback before assigning another gripper. Retained old camera frames
are hidden until the selected capture publishes fresh images.

Workspace/desk measurements and multi-camera calibration remain separate from
joint linking; see [the guided calibration roadmap](workspace-calibration-roadmap.md).

## Link the real robot and scene

Select **Scene robots**, select the PiPER + Pika model, then **Link robot**.
The first update places the scene at the measured real pose; linking does not move
the physical arm. After Enable robot, deliberate committed joint gizmo edits go
through the same validated controller commands as the dashboard. Measured feedback
does not generate input events and cannot echo back as another command. Recovery
keeps measured scene tracking alive but disarms scene-to-hardware commands until
the user recovers and explicitly enables the robot. Stale data, changed USB
connection generation, or a restarted backend discard motion permission.
Once a profile has both its scene robot and CAN adapter assigned, measured-only
tracking restores automatically after restart or scene reload. Explicit
**Unlink** remembers that tracking is off for that profile. Restoration never
replays targets, enables motors, resumes teleoperation or restores motion
approval. Choose **Use controls** to permit new commands when ready. Healthy
links do not repeatedly rediscover the scene or interrupt live dragging.
Use **Edit mode**, not Play. The scene's motion-graph preview yields while live
feedback owns the pose; its saved enable setting is preserved. Link status reports
the acknowledged joint error. Select an articulated link or its mesh, click the
viewport Rotate tool and drag the blue ring. Hinge joints show one ring aligned
to their actual MuJoCo axis, independent of global/local toolbar orientation.
Outside Teleoperate, releasing commits a target and cancellation sends none.
In Teleoperate, dragging streams targets while the button is held; release
finishes the final target at the chosen speed. Escape holds the measured pose.
Only the edited joint supplies a new target; the other five use fresh hardware
measurements, even if a scene preview has changed. For precise J6 tests, select
`link6`, put the pointer over the viewport, then use `R`, `Z`, `Z`, an angle in
degrees and Enter (local-Z rotation). Escape cancels before sending a command.
Translation of the whole robot is scene placement, not physical arm control.

The scene also follows the assigned Pika's fresh measured opening. The model
must expose opposite, equal-travel `joint7`/`joint8` slide joints in metres; the
two fingers receive half the opening in opposite directions. The current Pika
model exposes 90 mm of slide travel, covering the controller's 0–90 mm opening commands. Stale or
different-device feedback leaves the last simulated opening unchanged with a
status message. Openings outside the model range are reported rather than
clamped; small closed-position encoder noise maps to zero. The mapping uses
each slide joint's actual closed and open endpoints, including nonzero CAD
offsets, plus the selected robot's saved closed-display reference.
This is measured physical-to-scene feedback, not a calibration of fingertip
geometry or a command to replay gripper motion.

Linking may open a matching unowned Pika connection for read-only feedback even
while the arm is hand guided. This monitor sends no enable, disable or position
request and does not take over another client's live gripper session. The normal
Enable action grants this plugin's gripper control permission. Monitor-only
cleanup preserves externally enabled motor power; failed explicit power attempts
retain their separate ownership/uncertainty checks.

Recovery shows the recorded reason, joint and tracking gap. Its primary action,
**Resting & clear — recover to zero**, is also available beside the ordinary
zero control. Use it when the arm already rests securely on a surface or other
support and its route to zero is clear; no manual lifting into the command range
is required for the bounded resting overshoot.

Healthy enabled motors stay powered and move directly to zero. Healthy disabled
motors enable at their measured resting pose first. A controller STOP resets and
verifies all six motors off before enabling. Small range overshoot is corrected
at the existing 1% entry speed, then the requested zero move runs at 10%. Each
stage requires fresh feedback from the same controller request and connection.
An error or cancellation prevents later stages; no interrupted trajectory or
Pika target resumes. Progress is saved at
`recovery/<robot-profile>/last-recovery-to-zero.json`. Controller-only recovery
remains a secondary action. Cameras support human inspection; the plugin does
not infer physical support or clearance from pixels.

The Pika serial parser discards packets with duplicate fields, including the
intermittent repeated temperature/status fields observed on the real gripper.
Only an unambiguous packet refreshes telemetry age; bad packets do not tear down
the arm connection or silently pick one of two conflicting status values.

The initial profile requires `joint1` through `joint6` in radians. Scene identity
is its UUID, not a guessed name. Root transforms, arbitrary inspector edits and
physics playback are not robot target commands. Hand movement can update the scene
only when the robot is in a suitable supported backdrivable mode.

**Calibration is unverified.** Matching joint values does not prove matching tool
positions or camera pixels. Verify signs and zero offsets with small separated
joint movements, then calibrate the base/tool transforms and cameras using measured
desk fiducials. Use a known marker board for camera intrinsics/extrinsics and
independent check points; record residuals before enabling any contact task.
Never infer calibration success from a screenshot or automatically drive a desk-contact test.

Camera frames currently contain host receipt timestamps and sequence numbers.
They are previews, not hardware-triggered synchronized exposure or metric D405
depth recording. The D405 tile uses the official RealSense C# binding and defaults
to its ordinary color image, matching the web dashboard's video view. Its cog
offers **Color image (RGB)** or **Depth distance (7–50 cm)**, remembered per robot.
Depth is a measured-distance grayscale preview, with nearer surfaces brighter;
color is a regular image rather than colorized depth. The simulation counterpart
uses the engine's existing color or depth renderer to match the selected view.
Raw depth and device timestamps are not yet included in a dataset recorder.
All three cameras belong in the later
timestamp-aligned recorder.

## Install and build

Use a Lucky Engine build with plugin API 1. Obtain the public SDK `.nupkg` from
the plugin SDK release and add its directory as a NuGet source. Install .NET 9 SDK.

```powershell
dotnet nuget add source C:\path\to\sdk-packages --name LuckyEngineSdk
dotnet run --project managed/Piper.Plugin.Tests -c Release
powershell -NoProfile -File tools/Build-EnginePlugin.ps1
```

The Windows package contains the self-contained plugin, C# server, C# camera
worker, .NET runtime, OpenCvSharp/OpenCV, RealSense 2.59.1 native library, shared
web dashboard and native transport DLLs. End users do not install Python, pip,
FFmpeg, .NET or the RealSense SDK. Existing lens profiles and camera preferences
are preserved; obsolete Python/FFmpeg path settings are ignored.

The camera worker is isolated from the editor and has no robot-control references.
D405 color/depth frames and their separate factory intrinsics use the official
RealSense C# wrapper, selected by device serial. Changing views clears the previous
stream's cached profile before accepting new frames. Only supported rectified
pinhole profiles are applied to the simulation. A D405 color stream with nonzero
factory distortion is displayed normally, but the current native pinhole preview
cannot reproduce that distortion and keeps its lens/mount alignment unverified.
Factory intrinsics alone do not establish camera-mount or exposure alignment.
Bench and gripper color capture uses OpenCV DirectShow on Windows and V4L2 on Linux; named cameras are
resolved at connection time, with ambiguous duplicates rejected. Use the unique
DirectShow device path for identical cameras, and a stable `/dev/v4l/by-id/...`
path on Linux. Specify the serial when more than one D405 is connected.
Capture drains continuously and keeps a single latest preview in the plugin.
Physical camera latency still needs a live check after switching capture backends.

Developer packaging: native controller builds must already exist (see
managed/README.md). `Build-EnginePlugin.ps1` publishes the self-contained camera
worker and runs its offline dependency check. `Prepare-CameraRuntime.ps1` fetches
hash-pinned official artifacts and extracts the RealSense DLL without running
the SDK installer. Native license notices and the public C# wrapper sources are
included. No engine source is required.

Linux release builds must supply a RealSense 2.59.1 build for the target distro
at `build/linux-x64/librealsense2.so`; packaging fails if it is absent. Build it
with shared libraries and examples/tests disabled, and verify its `ldd`
dependencies in the release environment. OpenCvSharp uses its headless Linux
runtime. This Windows migration has not been validated on Linux; OS USB/udev
permissions remain an installation prerequisite. Windows N editions require
Microsoft's Media Feature Pack for Media Foundation components.

## Controller lifecycle and first-time setup

Pipers reuses a controller already listening on loopback ports 8765/8766. It does
not launch or stop an external dashboard server. If no controller is available,
the panel exposes **Controller setup > Start bundled server**. The start action
checks both ports and uses a per-user startup lock before launching a controller;
an occupied port is reported and never replaced or killed. Startup reads hardware
state; enable, recovery and movement remain explicit actions. Cameras work even
when no controller is running. The setup form collapses when feedback connects.

A server explicitly started by the plugin belongs to that plugin session. Closing
Pipers stops its exact active motion request, asks it to release USB transports
and exit, and leaves unrelated/external servers alone. This is a session controller,
not a persistent system service. It accepts control mutations only from the
starting plugin's owner ID; a second dashboard can read state but receives an
explicit shared-control error for mutation requests. This prevents another client
from acquiring a movement lease before the session-owned process exits. A web
dashboard opened against that session's server also loses its server when Pipers
closes. Use the separately managed dashboard launcher when the controller must
outlive the editor or be shared. No shutdown path
resets arm calibration or automatically enables/re-enables motors. Controller stop
can require the existing explicit recovery flow before further motion.

The bundled controller keeps state and bounded diagnostic logs in the current
user's LocalApplicationData/LuckyRobots/Piper directory; the directory is separate
from plugin packages and survives upgrades. Existing externally managed server
state is not moved or overwritten. WSL distribution and Pika serial device are
saved in plugin preferences and apply to the next bundled-server start.

The Pika Linux serial agent and its .NET runtime are included at
`server/pika-agent/Piper.Pika.Agent` on both platforms. Windows converts that
packaged path through the selected WSL distro's `wslpath` before launching it;
it grants the packaged agent's apphost its user execute bit using a direct WSL
`chmod u+x` command, without root. There is no `/opt` copy/install step. Linux
likewise ensures its packaged agent is executable and launches it
directly. Explicit `--pika-agent` overrides remain supported for standalone
controller deployments. The standalone Windows launcher retains its legacy
`/opt` fallback only when no adjacent packaged agent is present.

On Windows, the Pika serial transport requires **WSL 2**, a Linux distribution
(Ubuntu by default), **usbipd-win**, and explicit sharing of the Pika USB serial
device into that distro. The native Windows WCH serial driver remains unused by
the plugin because it caused machine crashes during development. **Check Pika
setup** checks the selected distro/device's write access without opening the serial
port or sending motor commands. It does not install WSL, attach USB devices,
change firewall rules or replace kernel drivers. Missing prerequisites are reported
with setup instructions; USB/admin changes must be performed explicitly by the
user or administrator. Keep the USB-CAN adapter native to Windows; share only
the Pika serial device. See `managed/README.md` for the documented usbipd/WSL
procedure. Windows USB-CAN must have its compatible WinUSB transport configured.
On Linux, configure the stable serial path and normal udev/group access instead.
Leaving the Pika serial device blank starts an arm-only controller.

Local developer package install (the store path is the installed editor's plugin
store; select Installed afterwards to activate the package):

```powershell
LuckyEngine.PluginManager.exe --install-local Pipers-0.1.0-preview.1-win-x64.zip --store C:\path\to\Plugins\packages --trust-local
```

For public distribution, publish the ZIP in this repository's GitHub Releases,
then fill `packages.windows-x64.url` and `sha256` in `lucky-plugin.yaml` using that
exact archive. The catalog entry alone is not a release and must not invent a digest.

## Live joint dragging

Link robot, Enable robot, then Start Teleoperate. Choose J1–J6 in the panel and
drag its blue rotation ring in the viewport. Keep the editor out of physics Play.
Normal scene selection is suppressed while Teleoperate is active. The solid
robot is the leader target; the cyan skeleton shows measured real joint geometry.
Release commits the final target and finishes the movement. The panel shows
Movement in progress and Stop movement (holds measured pose with motors enabled).
Escape, lost focus or Pause Teleoperate cancels. The solid robot retains the
destination while the translucent cyan mesh shows the measured real pose.
Cartesian tip dragging/IK remains a separate extension.

Targets replace one another at up to 20 Hz; the controller ramps at its existing
20 ms cadence using the selected speed and reported limits. Input older than
150 ms is discarded. Missing updates for 250 ms hold the measured pose, keeping
motors enabled when feedback remains valid. Existing fault/500 ms obstruction
stops remain active across retargets and release. Progress is measured from actual
displacement in the commanded direction, not error to a moving cursor target;
retargeting alone cannot renew the obstruction timer. A new gesture is required after expiry;
reconnecting never resumes an old target. These software checks are not a
hardware collision safety system.

Arrival uses the selected tolerance and fresh stationary feedback. If the MCU
retains its busy flag after reaching the final target, the controller verifies
all six measured positions and velocities, sends a legal measured-position hold
at 1%, and verifies another stationary feedback window before completing. This
keeps holding torque enabled without manufacturing a STOP/recovery incident.
Outside-tolerance obstruction and actual fault checks still apply.

Each real camera now has a selectable native simulation counterpart and optional
overlay under its cog. Bindings persist by camera UUID. Views preserve the real
frame aspect ratio; Lumina capture drains continuously and keeps only the newest
pending frame. The displayed pipe age excludes camera exposure and screen delay.
Camera mounts/lenses are estimated, not calibrated. In Teleoperate, sim previews
show the leader scene, not timestamp-matched measured observations. The D405 sim
preview uses the engine's existing clean metric depth pass. Both depth previews
use a fixed 7-50 cm grayscale scale: white is near, black is invalid/outside the
range. This is ideal geometry depth, not a reproduction of stereo holes/noise.
Pika uses the engine's path-traced fisheye/Kannala-Brandt model.

## Camera calibration (included for every plugin installation)

Open the Pika camera cog > Open printable board. Print at actual size and verify
the 100 mm reference with a ruler; attach the sheet to a flat rigid backing.
Keep the robot stationary. Capture at least 20 sharp views of the whole board
at varied positions, tilts and distances, including image edges, then select
Solve & validate fisheye lens. No robot motion is sent by this workflow.
Choose Check camera runtime to verify the bundled libraries without opening a
camera or downloading anything. Missing/damaged libraries produce a reinstall
message. The C# solver runs in the background with a five-minute timeout; closing
the plugin cancels only its worker. System packages stay unchanged.
No engine source checkout is required. New capture session preserves prior images
and the accepted profile while starting a fresh set of board observations.

The solver reserves every fifth accepted image for validation, rejects weak
coverage/tilt and excessive reprojection errors, and writes the fitted matrix,
four distortion coefficients, device identity, resolution and residuals into
the plugin's camera-calibration settings folder. Raw captures and reports remain
there for inspection. Profiles are applied only to a matching device and size;
use a stable device path when multiple identical USB cameras are connected.
D405 intrinsics are read from its selected stream's factory profile automatically.
The native plugin API applies focal lengths and principal point without editing
the saved scene camera. A saved lens profile persists across plugin upgrades.

Lens calibration is only the first step. Camera-to-gripper and bench-to-world
transforms still need a measured board pose in the scene and several independent
physical poses. The panel continues to mark mounts and timing unverified. Joint
values alone do not validate optical extrinsics. Real RGB appearance, sensor
noise and stereo depth holes cannot be promised pixel-identical to a rendering.
Do not use these previews as a synchronized calibrated dataset yet.

Offline development checks (no camera or robot is opened):

```powershell
dotnet run --project managed/Piper.Camera.Tests -c Release
dotnet run --project managed/Piper.Plugin.Tests -c Release
# Optional fixture with known synthetic lens K=(245,247,315,198), 640x400:
dotnet run --project managed/Piper.Camera.Tests -c Release -- path\to\synthetic-session
```

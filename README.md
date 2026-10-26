# PiPer Direct C

Native Linux C17 controller and local web GUI for **one selected PiPer arm**. Robot communication, motion scheduling, diagnostics and HTTP run in C over SocketCAN. No Python, ROS, AgileX SDK or Node process is used by the controller. The browser uses plain HTML/CSS/JavaScript; cJSON is the only vendored library.

## Run

```sh
make
./build/piper-control --port 8765 --state-dir data --web-root web
# Open http://127.0.0.1:8765/
```

Stop other robot-control software first. Startup only discovers adapters and listens. CAN links must already be UP at **1 Mbit/s**, using normal classical CAN. Link configuration is verified through Linux netlink. The daemon does not configure interfaces, reset USB devices or enable motors on startup.

On this computer, the separate hotplug setup below activates the three assigned USB-CAN adapters when plugged in or discovered during boot. The C daemon remains unprivileged.

An independent simulator never opens real CAN sockets:

```sh
./build/piper-control --simulate --port 8877 --state-dir data-sim --web-root web
```

Simulation is clearly labeled. Hardware and simulation state directories cannot be interchanged. Persistent recovery records are in `data/control-state.json`; do not delete this file to bypass recovery.

## GUI workflow

1. Select the arm's USB adapter in the sidebar. `--legacy-dir /path/to/old/dashboard/data` imports names and stop records read-only. Adapter serials identify **adapters**, not arms; keep their physical assignments consistent.
2. The dashboard reads firmware and limits automatically when fresh robot feedback appears, and refreshes limits before they expire. **Read firmware & limits** is also available manually. Unknown readings stay unknown. All six positions, controller status and all six motor statuses must be fresh before control. All six acceleration limits must also have been read within 60 seconds and be greater than zero and at most 5 rad/s² (the conservative operating cap for this controller). Limits are read only; no settings are written.
3. If a saved recovery record remains but the motors are already off and healthy, confirm support and click **Verify motors off**. This verifies fresh, stationary, disabled feedback and clears the record without sending a CAN command. A pending reset or hardware fault cannot be cleared this way. Inspect any fault/stop. **Stop recovery → Reset controller** sends one reset, removes holding torque and verifies fresh disabled, stationary feedback. A pending reset is never automatically resent. **Verify completed recovery** is for an operator-confirmed reset/power cycle already performed.
4. Confirm the operating conditions and physically support the arm before **Enable motors**. The controller explicitly addresses drivers 1–6 and checks their enabled feedback; it sends no position target or mode command. A resting joint outside the command range does not prevent enabling. Unexpected movement during enable still triggers stop. A successful normal Disable allows Enable again; disabling after an existing stop retains the recovery record.
5. If a resting joint is outside its command range, use **Move into range** after enabling. The displayed correction places only the affected joints 0.5° inside their command ranges, capped at 3° of correction per joint and 1% MCU speed. This explicit entry move uses a boundary target because intermediate out-of-range targets are invalid; it does not use the normal 1° lead limit. Other joints keep their measured starting targets. Larger corrections are rejected.
6. Enter an absolute angle or draft it with a slider, then click the joint's **Move**. **− step / + step** jog from its current measured angle. **Zero** requests 0° within the reported limits; it never rewrites calibration. Other joints retain their measured starting targets.
7. **Move pose** executes the six displayed targets. **Copy measured pose** only fills inputs. **Hold here** changes an active move to the current measured pose. Gripper controls read the configured 70 or 100 mm maximum from the arm before allowing motion. Effort is adjustable from 0.02–0.5 N·m (default 0.1). A raw closed reading between −2 and 0 mm requires an explicit acknowledgment to open to at least 5 mm; this sends a bounded 1 mm entry target and does not alter encoder zero. Raw feedback stays visible.

**Upright & return** performs one complete movement after motors are enabled and the arm is in range: capture the measured starting pose, raise to `[start J1, 82, -160, 0, -7, start J6]` degrees, verify arrival, pause for two seconds, then return to the captured pose. Base heading and tool roll stay fixed. The preset comes from the earlier dashboard and [AgileX's modified-DH model](https://github.com/agilexrobotics/piper_sdk/blob/c9e8a28174e71eeaac448593cb65f8ab258a92fe/piper_sdk/kinematics/piper_fk.py); it is not a vendor-named pose. Each leg uses synchronized joint interpolation at up to 3°/s, a 1° feedback lead bound, and 0.2° arrival tolerance. The cycle uses a fixed speed rather than the individual-jog speed setting, with a 180-second total timeout. For a folded starting pose, allow roughly two minutes. Clear the entire swept path and keep the dashboard focused; this is joint-space motion, not collision planning. Stop or Hold cancels the remaining sequence, including the return. No motion resumes automatically. Gripper position is unchanged and motors remain enabled on success.

Keyboard: **1–6** select a joint; **←/→** jog outside input fields; **Esc** requests stop. Sliders and number fields only draft targets until Move is pressed. Keep the dashboard focused and visible during an action. Losing focus, hiding/closing the page or losing the heartbeat ends the action and requires recovery.

The large angle is measured, not predicted. Commands have 0.001° wire resolution. Completion requires the final target to have been transmitted, subsequent fresh feedback within tolerance for 0.4 seconds, and controller target-reached status. Default tolerance is 0.2°, adjustable from 0.05–1°. Encoder resolution is **not** guaranteed physical accuracy: calibration, backlash and load still matter. There is no external metrology.

## API

The GUI and external clients use the same API. The [OpenAPI specification](web/openapi.json) is served at `/api/openapi.json`.

| Route | Purpose |
| --- | --- |
| GET /api/state | Discovery, stable IDs, generations, angles, driver/current/temperature/voltage feedback, gripper, firmware, limits, blockers and job state |
| POST /api/select | Select one detected adapter without motion |
| POST /api/query | Bounded firmware and six limit reads |
| POST /api/reconnect | Reopen sockets, clear feedback, refresh selected arm's firmware/limits; require explicit control |
| POST /api/command | joint, jog, zero, pose, align, cycle, gripper, enable, disable, hold, reset, verify-reset, verify-disabled |
| POST /api/heartbeat | Renew the owner/request-ID control lease |
| POST /api/stop | Best-effort software stop and durable recovery lock |

Every POST requires `Content-Type: application/json` and `X-Piper-Token` from GET state. Commands bind to the selected `device_id` and current connection `generation`. Use a unique `owner` string of 16–80 characters. Asynchronous actions return `request_id`; send that ID and owner to heartbeat every 200 ms and poll state for completion. HTTP 202 means **accepted**, not reached. Do not automatically retry actuator/reset requests after an ambiguous response; inspect state first.

Example body after resolving identity/generation and enabling:

```json
{
  "device_id": "usb:YOUR_ADAPTER_SERIAL",
  "generation": 1,
  "owner": "YOUR_UNIQUE_CLIENT_ID",
  "action": "joint",
  "joint": 1,
  "degrees": -12.5,
  "speed_deg_s": 3,
  "tolerance_deg": 0.2,
  "standalone": true,
  "clear": true
}
```

Acknowledgements must reflect actual conditions. Power/recovery commands additionally require `supported: true`; manual recovery verification needs `reset_completed: true`.

This is a complete API for the **implemented position-control workflow**, not every proprietary robot function. It has no arbitrary CAN writer, encoder-zero writes, MIT torque/current/gain control, Cartesian inverse kinematics or firmware flasher. The standard gripper implementation is not a PiKA camera-gripper integration.

## Passive demonstration capture

`make` also builds `build/piper-record`, a native C listener that discovers the
specified USB-CAN adapter and records measured joint positions, raw gripper
feedback, teaching state, driver readings and host timestamps at nominal 50 Hz.
It sends no robot CAN commands and can run alongside the dashboard or onboard
teaching replay. It never resets teaching or changes the stored demonstration.

```sh
./build/piper-record --device usb:004000485246570520323934 \
  --seconds 60 --out demonstration.jsonl
# Optional packaging, Parquet export and timestamped bench-camera frames:
python3 tools/capture_episode.py --device usb:004000485246570520323934 \
  --seconds 60 --camera --out artifacts/my-new-demonstration
make test-record
```

The packaging script requires `pyarrow`; robot communication remains in C.
Camera capture reads the already-running dashboard camera and includes host
request/response times. Existing output files/directories are rejected. Adapter
replacement, stale joint feedback, interruption and write failures produce an
incomplete recording instead of a successful episode. Duration is bounded to
600 seconds. This tool does **not** initiate onboard replay.

These files contain **observations**, not issued action targets or a downloaded
firmware recording. They are not labeled as native LuckyEngine datasets. Negative
gripper feedback is preserved, never silently clamped into a control command.
Parquet export verifies a data-and-metadata round trip and records hashes. A
completed capture does not establish cup-pickup success. Comparing it against a
LuckyEngine episode requires the same trajectory, validated joint/gripper
mapping and explicit timing alignment; host snapshots are not hardware-clock
synchronized.

## Bench webcam

The sidebar's **Start camera** opens the configured Logitech C930e. **Enlarge** provides a larger live view while keeping the top STOP button accessible. Video stays on this computer; there is no recording or microphone capture. The live label requires fresh camera frames; disconnected or stalled capture is marked unavailable.

Pass `--camera-device /dev/v4l/by-id/YOUR-WEBCAM-video-index0` to configure capture. The installed service uses the existing Logitech's stable path. Camera start/stop uses authenticated `POST /api/camera` with `{"enabled":true}` or `false`. `GET /api/state` includes camera status; `GET /api/camera/frame` returns a current JPEG or 409 when no fresh frame exists. Simulation never opens the real camera.

The C server supervises the installed `/usr/bin/ffmpeg` as a separate capture process, copying the camera's MJPEG frames at 1280×720/15 fps. Browser viewing is about 5 fps. Capture is off at daemon startup, stops with the daemon, and cannot block the CAN/control loop: pipe reads are nonblocking and bounded. This adds a video utility dependency only; robot communication remains entirely native C without an SDK. If another app owns the camera, close that view and press Start camera again.

## Direct control and protocol

The host sends CAN MoveJ mode at `0x151` and joint targets at `0x155–0x157`; angle feedback arrives at `0x2A5–0x2A7`. The onboard MCU still coordinates motor drives. Replacing the SDK does **not** physically bypass that MCU or its motor loops. Single-joint control sends a six-joint target vector with five targets held constant.

Position support is restricted to hardware H-V1.2-1 and the reviewed S-V1.6-3…1.8-9 protocol family. This is a protocol compatibility gate, not proof that every revision has been physically tested. Unknown revisions stay read-only. Hardware limits are read from the controller, never guessed, and must be recent at the start of an action.

Default host ramp is 3°/s, selectable from 0.1–15°/s and limited by reported motor maximums. CAN MoveJ speed is also capped at 10%. Commanded lead is limited to 1°; a measured path deviation over 3°, five seconds without progress, a 120-second motion timeout or driver/mode/feedback failure stops the action. These are software bounds, not certified safety controls. Joint limits do not prevent collisions.

Current feedback is signed amperes, velocity signed radians/second, voltage volts and motor temperature °C. Current is not an independent calibrated torque measurement. Gripper motion uses 5 mm/s and a 0.5 N·m effort limit. Queued frames are not execution acknowledgements; Linux transmission counters and robot feedback are shown separately.

## Connection and stop behavior

- Adapter discovery runs every second. Only the selected arm receives action commands. USB identity and connection generation bind each job. Reconnection invalidates feedback and never resumes motion. The selected adapter persists across restarts; if it is absent, it remains selected and marked missing instead of another arm being substituted.
- The dashboard's Reconnect button automatically requests firmware and limits again on the selected, available adapter. This is one bounded information query, not a motor command or a retry loop. A missing selected adapter is not replaced with another arm. Startup and physical hotplug remain passive; use Read firmware & limits after those events.
- Joint/controller/driver readings expire after 350 ms; control leases expire after 800 ms. The nonblocking HTTP loop keeps checking CAN and leases while a client sends a slow/incomplete request.
- An interrupted-action record is saved before actuator commands. A process crash leaves recovery required on restart. Storage errors block control.
- Foreign control traffic and multiple feedback address ranges block motion. Only one native controller per user can run. Other processes/users with CAN access must not control the same arm.
- **STOP is a software request, not a physical emergency stop or power cut.** It may permit damped descent. A failed link prevents guaranteed delivery or physical stopping. Keep the hardware stop accessible and support the arm before power changes.
- Shutting down during an action attempts stop. Shutting down while idle leaves the existing motor enable state unchanged.
- A reset that may have been transmitted stays pending until verified; it is not retried automatically.

Zero frames in both this controller and the old dashboard locate missing data below their decoders. They do not identify the cause: wiring, adapter/driver behavior, arm mode, power and firmware remain possible.

## Background service on this computer

[The service override](deploy/40-direct-c.conf) runs this daemon through the existing boot-enabled `piper-calibration-studio.service`, retaining the familiar address and tray launcher. The previous Python dashboard is stopped when this override is active. The existing Python **tray launcher is read-only**; all robot control runs in C.

```sh
systemctl --user status piper-calibration-studio.service
journalctl --user -u piper-calibration-studio.service -n 50
```

Rollback: first ensure no action is active and the arm is supported/disabled. Stop the service, remove only its `40-direct-c.conf` override, reload systemd and restart. The earlier repository override and original calibration data remain intact. **C recovery records are separate:** physically recover and verify after any unresolved C stop before changing back to a controller that does not know that record.

### Activate CAN after USB reconnection

USB re-enumeration creates a new CAN link with no bitrate and a DOWN state. Socket reopening alone cannot recover that link. [The udev rule](deploy/80-piper-can.rules) starts [a bounded system service](deploy/piper-can-setup@.service) on device addition. Its [helper](deploy/piper-can-setup) checks the USB vendor/product and these three assigned adapter serials, then configures a DOWN link at 1 Mbit/s and brings it UP. Already-UP links are left untouched. It has no CAN socket, sends no robot frames, never resets USB and does not restart failed setup in a loop. The motion stop/recovery record remains in force.

Install these root-owned files after checking that their serial assignments match this installation:

```sh
sudo install -D -o root -g root -m 0755 deploy/piper-can-setup /usr/local/libexec/piper-can-setup
sudo install -D -o root -g root -m 0644 deploy/piper-can-setup@.service /etc/systemd/system/piper-can-setup@.service
sudo install -D -o root -g root -m 0644 deploy/80-piper-can.rules /etc/udev/rules.d/80-piper-can.rules
sudo systemctl daemon-reload
sudo udevadm control --reload-rules
```

No service enable command is needed: [systemd device activation](https://github.com/systemd/systemd/blob/v255/man/systemd.device.xml) invokes setup on boot/hotplug. For an already attached, inactive link, run `sudo systemctl start piper-can-setup@can0.service` after resolving its identity. Setup logs: `journalctl -u piper-can-setup@can0.service`. Removing these three installed files, reloading rules and reloading systemd disables future automatic setup without taking a live link down.

## Independent connection probe

`build/piper-can` remains a separate diagnostic executable:

```sh
./build/piper-can --scan --seconds 5
./build/piper-can --watch
./build/piper-can --interface can0 --query-firmware --seconds 5
```

Default probing is passive. Firmware query sends one read without replay after reconnection. This executable has no motor commands. Exit 0 means a fresh complete joint/status set was received; 2 means it was not established; 1 indicates a usage/system error. Fresh readings alone do not prove readiness to move.

## Validation

Build: Linux SocketCAN headers, C17 compiler, make, libc/libm. Tests additionally use Node.js. Virtual-CAN tests need iproute2, ripgrep, kernel vcan and permission for `sudo -n ip`.

```sh
make test
make test-vcan
```

Tests cover protocol signs/units, invalid packets, freshness, faults, modes, limits, foreign controllers, generations, local echoes, durable interruptions, HTTP origin/token checks, exact signed simulated targets, retention of other joints, pose/gripper/power workflows, slow ramp completion, heartbeat loss, slow HTTP clients, reconnects and crashes. Simulation does not validate physical movement.

Hardware check, 2026-10-05: physically replugging PiPer 1's USB adapter and activating its newly created CAN link restored fresh controller, six-joint, six-driver and gripper feedback. Firmware and all six limit reads succeeded: H-V1.2-1 / S-V1.8-2, build 2025-10-14. An independent passive C probe also received complete feedback. PiPers 2 and 3 still had zero received frames. No actuator commands were sent in this check; physical motion remains unverified and the recorded stop stays active. Hotplug rule matching and helper behavior are checked separately; a subsequent physical replug/boot is needed to verify automatic activation end to end.

Subsequent dashboard/camera check: the operator identified PiPer 1 as the back-left arm and confirmed support/clearance. The Logitech stream, Enlarge, Stop camera and restart were exercised through the visible dashboard. Stopped video returns HTTP 409 instead of a stale JPEG. All 20 robot-status samples stayed fresh during video, with a maximum sampled response time of 30 ms. Dashboard reset completed with all six motors disabled and cleared the recorded recovery lock. Motor enable was rejected before transmission because the measured shoulder position was below its reported 0° minimum; manual repositioning changed the encoder readings but the last observed J2 remained −1.869°. That startup restriction was corrected in the October 8 check below.

References: [pinned official AgileX message definitions](https://github.com/agilexrobotics/piper_sdk/tree/c9e8a28174e71eeaac448593cb65f8ab258a92fe/piper_sdk/piper_msgs/msg_v2), [high-speed feedback](https://github.com/agilexrobotics/piper_sdk/blob/c9e8a28174e71eeaac448593cb65f8ab258a92fe/piper_sdk/piper_msgs/msg_v2/feedback/arm_feedback_high_spd.py), [low-speed feedback](https://github.com/agilexrobotics/piper_sdk/blob/c9e8a28174e71eeaac448593cb65f8ab258a92fe/piper_sdk/piper_msgs/msg_v2/feedback/arm_feedback_low_spd.py), [Linux SocketCAN](https://docs.kernel.org/networking/can.html). The SDK is reference material only; it is not imported or linked.

Enable sequence reference: [AgileX EnableArm / EnablePiper](https://github.com/agilexrobotics/piper_sdk/blob/c9e8a28174e71eeaac448593cb65f8ab258a92fe/piper_sdk/interface/piper_interface_v2.py#L3062). This is a protocol reference only; the C daemon does not load or run the SDK.

Hardware check, 2026-10-08: PiPer 1 enabled from its out-of-range resting pose using only driver power commands; all six measured angles stayed unchanged. Explicit range entry moved J2 from −1.983° toward +0.500° (measured +0.422°) and J3 from +1.605° toward −0.500° (measured −0.427°). Each joint then passed an individual 0.5° test at 1°/s with 0.1° feedback tolerance; final errors were 0.045°, 0.069°, 0.035°, 0.051°, 0.053°, and 0.044° for J1–J6. Other joints retained their readings during each test. Hold and normal Disable were verified. The webcam was live throughout. These are encoder-based checks, not independent physical accuracy measurements; gripper movement and full travel remain unverified. Evidence: `artifacts/hardware-range-entry.json` and `artifacts/hardware-small-tests.jsonl`.

[AgileX high-speed feedback parser](https://github.com/agilexrobotics/piper_sdk/blob/c9e8a28174e71eeaac448593cb65f8ab258a92fe/piper_sdk/protocol/protocol_v2/piper_protocol_v2.py#L120) decodes signed motor current. Startup uses accumulated measured stillness; an old MCU target-arrival flag does not prevent enabling after the disabled arm settles.

Final October 8 checks: re-enable after settling, software STOP, completed-reset verification, and the corrected reset sequence all passed on PiPer 1. Reset now waits for normal controller status during its bounded observation window instead of sending an extra stop during a transient MCU status change. Final state: all six motors disabled, no reported faults, recovery lock cleared, live camera.

### Supervised simulation-to-hardware trials

`pose` accepts `synchronized: true` to use a shared interpolation fraction across all six joints. Existing speed, feedback-lead, timeout and lease guards still apply. During a gripper ramp, its owner may issue `hold`; this ends the ramp while retaining the last transmitted target and effort. It does not certify contact or a secure grasp. Fresh gripper effort is exposed as `effort_nm`; active command targets are exposed separately from measured positions.

`build/piper-trace --device usb:SERIAL --out NEW.jsonl --seconds 180` passively records control frames and information requests, with host receive timestamps and local/remote origin. It sends no robot commands. A complete footer requires no observed receive-queue drops or connection failure. Local echoes prove issued frames, not physical execution. Pair this trace with the existing passive measurement/camera recorder for a command-based comparison.

`tools/supervised_action.py` previews one reviewed request by default. Execution requires explicit `--execute --attended`, and power changes also require `--supported`. It maintains the C controller lease, verifies the stable device/generation and camera, records status, and stops its own active action on a supervision failure. It never bypasses the C controller or imports a robot SDK.

Teaching recovery diagnostics (October 10): `/api/state` now exposes `teach_state`, joint communication/angle fault masks, per-joint acceleration limits and their freshness, and a persisted `last_fault`. The live status can return to normal after a transient fault; the saved fault and recovery lock remain. Successful information refresh does not clear or conceal an unresolved stop. The dashboard shows standby/teaching state independently of the physical LED. The information query adds six `0x472` content-2 read requests and decodes `0x47C` replies in 0.001 rad/s² units. Unsupported, zero, missing, future-dated or stale acceleration limits block new motion and motor enable.

PiPer 1 currently reports S-V1.8-2. AgileX documents legacy mode-transition/reference bugs in [discussion 58](https://github.com/agilexrobotics/pyAgxArm/discussions/58) and [issue 114](https://github.com/agilexrobotics/piper_sdk/issues/114). An all-six joint communication fault occurred about 0.52 seconds after the first MoveJ command following teaching on October 10. This is a suspected firmware handoff issue, not an established root cause. Do not automatically retry, discard the recovery record, infer readiness from the LED, or write encoder zeros/firmware parameters as a workaround.

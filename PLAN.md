# PiPer C terminal controller plan

**Status: historical design proposal. The later request expanded implementation to a native C daemon, web GUI, six-joint position API and gripper controls; see [README.md](README.md) for current capabilities and validation. The GUI supports joint-selection/jog keys; a standalone ncurses terminal and CPV/torque control remain unimplemented. No robot commands were sent to prepare this original proposal.**

Build a separate Linux application in C for one PiPer arm. It will communicate through SocketCAN, display live feedback for all six joints, and let the operator select a joint with `1`–`6`, change its position with the arrow keys, or enter an absolute target. There will be no vendor SDK, Python runtime, webcam, or dependence on the existing dashboard.

The recommended sequence is: establish trustworthy read-only diagnostics; prove small joint movements with the normal joint-position protocol; then validate the individual-joint CPV interface. CPV is the closest documented match to the requested actuator-by-actuator control. Both approaches can be implemented entirely in C. They still communicate through the arm's existing firmware.

## 1. Answers to the main questions

| Question | Finding and proposed approach |
| --- | --- |
| Can we control it without the Python SDK? | **Yes.** The official source exposes the CAN message layouts. A C program can encode and decode them directly using Linux SocketCAN. |
| Does each actuator report its current position? | **Yes.** Joint-angle feedback covers all six joints. Separate motor messages report position, velocity, and electrical current. Display measured position separately from the requested target. |
| Can we trust those values? | Use fresh, correctly decoded feedback as the controller's reported state. It is not an independent measurement of physical accuracy or proof that calibration is correct. Validate units, signs, zero alignment, and movement against the real arm before relying on it. |
| Can a joint move to zero? | **Yes, by requesting position 0 in its existing coordinate system.** That is different from overwriting its calibrated zero. It does not necessarily mean an upright arm or a clear travel path. |
| Can we command individual joints? | Normal MoveJ accepts six joint targets. CPV has a separate CAN address for each joint's position/velocity commands. MIT mode also addresses individual joints and exposes position, velocity, gains, and feed-forward torque. |
| Can we remove the arm's internal controller? | Removing the host SDK does not remove the arm controller. The public protocol supports lower-level commands, but the inspected sources do not establish a supported electrical bypass of the internal controller. That would be a separate hardware project. |
| Does C eliminate connection problems? | It simplifies the application and makes its behavior easier to audit. USB adapters, the hub, Linux drivers, CAN wiring, and arm firmware remain. Diagnostics must identify which layer failed. |

These findings come from the official [legacy PiPer message definitions][legacy-protocol], the newer [PiPer driver implementation][driver], and [Linux SocketCAN documentation][socketcan].

One correction to the earlier hypothesis: the existing dashboard already uses raw SocketCAN from Python; its hardware module does not import `piper_sdk` or `pyAgxArm`. This separate project removes the existing application and Python runtime from the control path. It will not, by itself, prove that an SDK caused the earlier disconnections.

## 2. What remains inside the robot

The proposed communication path is:

```text
Keyboard and terminal display
          |
Our C program: input, limits, diagnostics, CAN codec
          |
Linux SocketCAN and USB-CAN kernel driver
          |
USB-CAN adapter and external CAN cable
          |
PiPer arm controller and its installed firmware
          |
Joint motor drivers, encoders, motors, and gearboxes
```

| Component | Responsibility and evidence boundary |
| --- | --- |
| Our C application | Selects the arm, interprets keyboard input, maintains targets, checks feedback and limits, emits documented messages, and records what happened. |
| Linux and the USB-CAN adapter | Transport CAN frames and report available link/controller errors. SocketCAN is an operating-system interface, not a robot SDK. |
| PiPer arm controller | The public API exposes control ownership, motion modes, coordinated joint/Cartesian motion, configuration, status, and faults. Firmware versions also change command interpretation and torque scaling. Cartesian IK feedback on newer firmware demonstrates that computation happens within the arm. |
| Joint motor drivers | Report motor state, driver status, temperatures, and enable state. The FOC status fields and control interfaces indicate local motor-control electronics. The exact allocation of current, velocity, and position loops between boards is not established by the inspected public source. |

The firmware reference explicitly assigns part of torque scaling to the arm master controller on newer releases. This is concrete evidence that an individual-joint command is not necessarily passed through unchanged. The public host source is not a complete specification of the embedded firmware, its timing, or its safety behavior. [Firmware reference][firmware], [driver status fields][low-feedback].

**Boundary for this project:** replace host software and send CAN commands ourselves. Do not rewire an internal motor bus, replace firmware, or assume access to bare motor PWM/current loops. Exact MCU model, internal bus wiring, motor part numbers, gear ratios, encoder arrangement, brake behavior, and certified torque/accuracy ratings remain unverified.

## 3. The six actuators and their available feedback

The existing dashboard's cached PiPer 1 information reports firmware **S-V1.8-2**, hardware **H-V1.2-1**, and a firmware build dated **2025-10-14**. Its cached joint limits are shown below. These are observations from the current application, not fresh measurements made by the proposed C controller.

| Selection | Joint | Cached angular range | Joint-angle feedback | Motor / driver feedback | Individual CPV address |
| --- | --- | --- | --- | --- | --- |
| `1` | J1 | −150° to +150° | `0x2A5`, first value | `0x251` / `0x261` | `0x181` |
| `2` | J2 | 0° to +180° | `0x2A5`, second value | `0x252` / `0x262` | `0x182` |
| `3` | J3 | −170° to 0° | `0x2A6`, first value | `0x253` / `0x263` | `0x183` |
| `4` | J4 | −100° to +100° | `0x2A6`, second value | `0x254` / `0x264` | `0x184` |
| `5` | J5 | −70° to +70° | `0x2A7`, first value | `0x255` / `0x265` | `0x185` |
| `6` | J6 | −180° to +180° | `0x2A7`, second value | `0x256` / `0x266` | `0x186` |

The CAN mappings are defined in the official [joint feedback][joint-feedback], [motor feedback][high-feedback], [driver feedback][low-feedback], and [CPV messages][cpv-messages]. The six joints share these protocol capabilities; that does not imply identical motor ratings or gearing.

The cached configuration also reports 0.3 rad/s maximum speed and 0.5 rad/s² maximum acceleration for each joint. Treat these as configuration values to re-query and validate, not manufacturer-rated limits or recommended test speeds.

**Left and right mean decreasing and increasing the numerical joint angle.** They do not mean physical movement toward the left or right of the room. J2 and J3 have asymmetric ranges: a negative absolute J2 target or positive absolute J3 target may be invalid. At a limit, reject the next step with a readable explanation; do not wrap around.

### What the readings mean

| Reading | Available information | How the terminal should treat it |
| --- | --- | --- |
| Joint angle | Six signed values, encoded in 0.001° units in `0x2A5`–`0x2A7`. | Primary displayed joint position. Encoding resolution is not physical accuracy. Track freshness for each joint pair. |
| Motor position and velocity | High-rate per-motor messages. The newer parser scales both by 0.001, presenting position in rad and velocity in rad/s. | Retain raw values and validate the relationship to joint-angle feedback and observed motion. Do not silently substitute one position source for another. |
| Motor electrical current | High-rate messages, scaled to amperes. | Useful for load changes and diagnosis. Some comments describe it as unsigned, while both inspected parsers decode a signed 16-bit value. Use the implementation as a starting point and validate sign/units on the target firmware. |
| Estimated torque | Calculated by the newer SDK from current and model-specific coefficients. | Label **estimated torque**, with the coefficient/profile used. It is not a separate torque sensor or a direct measurement of external contact force. Keep it unavailable until the profile is verified. |
| Bus voltage and current | Driver feedback provides voltage and a separate bus-current field. | Distinguish motor current from bus current. The source says some smaller joint motors do not measure bus current and report zero; zero is not proof of no load. |
| Temperatures and driver flags | Driver/motor temperatures; undervoltage, overcurrent, overheating, driver faults, enable state, collision/stall flags. | Show the actual flags and their age. A missing flag is not proof that a mechanical path is collision-free. |
| Arm-level status | Current control mode, motion mode/state, teaching state, and error code. | Show alongside joint state. Healthy CAN traffic does not establish that the arm is accepting motion commands. |

Sources: [decoder implementation][parser], [feedback definitions][high-feedback], [driver feedback][low-feedback], and [torque coefficients][constants].

For trustworthiness, require a coherent set of recent joint messages, recent arm status, and valid motor/driver state before motion. Missing data displays `unknown`; stale data keeps its last value visibly marked stale. Startup zeros must never masquerade as measured zeros. Successful `send()` means the kernel accepted a frame, not that the robot reached the requested angle. Completion needs feedback within a defined tolerance for a defined dwell time.

### The gripper

The standard PiPer gripper has its own command/feedback interface (`0x159` / `0x2A8`), including position/opening, effort or force-related values, enable/fault information, and zero calibration. Interpretation depends on gripper and firmware; newer definitions distinguish width and angle modes. These values are not automatically a calibrated fingertip force measurement. [Gripper feedback][gripper-feedback], [gripper driver][gripper-driver].

The first release controls J1–J6 only. It may display gripper feedback passively, but will not enable or move the gripper. PiKA camera-gripper support is a separate integration and is not implied by standard PiPer gripper support.

## 4. Position control, CPV, and MIT

| Interface | What we command | Role in this project |
| --- | --- | --- |
| Normal joint motion, MoveJ | Six absolute joint angles, with arm motion settings. The arm handles the joint move. | First motion baseline. Change one selected target and retain the other five targets. No inverse kinematics is needed in our program. |
| CPV position | One joint's position through its individual address. CPV also exposes profile velocity, acceleration/deceleration, and loop parameters. | Second milestone: the intended individual-actuator position interface, after firmware and transition behavior are proven. |
| CPV velocity | One joint's velocity. | Deferred. A lost connection during velocity control needs a verified embedded timeout/stop mechanism; a host-only timeout cannot stop a disconnected arm. |
| MIT control | Per-joint desired position, velocity, position/velocity gains, and feed-forward torque. | Advanced, deferred. This changes control behavior substantially and requires validated gains, torque scaling, and timing. |
| Joint pass-through, MoveJS | Joint commands with different planning/stiffness behavior. | Excluded initially. It is not interchangeable with MoveJ, and the vendor documents older-firmware mode-transition caveats. |

The [driver implementation][driver] and [API documentation][api] expose these capabilities. CPV position is a realistic way to get closer to the requested individual actuator control while retaining the existing embedded motor electronics. We should not describe it as a proven bypass of all arm-level planning or protections.

For MoveJ, seed all six targets from a fresh, stationary measured pose during an explicit control handover. Then change only the selected target. Do not fill unselected joints with zero or continuously replace their targets with noisy feedback. Verify the other joints stay within the agreed hold tolerance. The six targets span three CAN frames; transmission is not atomic. A partial send is a control fault, and firmware behavior in that case needs verification.

### Firmware details that affect a C implementation

Your cached S-V1.8-2 version selects the older protocol family provisionally. Query it again before enabling control.

| Firmware family | Relevant difference |
| --- | --- |
| S-V1.8-2 and earlier | Older MIT encoding with 8-bit torque and a checksum; host-side torque scaling differs from later firmware. |
| S-V1.8-3 through S-V1.8-7 | Similar MIT frame layout, but the master controller takes over part of torque scaling. |
| S-V1.8-8 onward | Revised MIT encoding uses 12-bit torque without that checksum; mode/status interpretation also changes. |

The inspected firmware reference lists CPV across these driver families. This is evidence of software support, not a completed test of every operation on this particular arm. Model-specific sign corrections also exist, including CPV velocity sign handling. [Firmware reference][firmware], [versioned driver source][version-drivers].

Two concrete inconsistencies justify an explicit compatibility layer:

- The newer SDK's degree defaults give standard PiPer J6 ±180°, while its radian defaults correspond to ±120°. The C program must use verified hardware/model constraints and fresh controller configuration, never blindly trust a generic SDK default. [Constants][constants].
- The high-rate current comments and decoder disagree about signedness, as described above. Store raw payloads and test conversion assumptions instead of copying comments unquestioningly. [Feedback definition][high-feedback], [parser][parser].

CPV deserves additional care: the current SDK switches motion mode inside some CPV query helpers and repeats the first position write. Those are behaviors to understand and test, not hidden side effects to reproduce in a passive monitor. CPV tuning/configuration writes can persist to motor flash; normal startup and keypresses must never rewrite gains or persistent limits. [CPV implementation][cpv-driver], [CPV API][cpv-api].

## 5. Zero, enable, disable, and stop are separate actions

**Go to zero:** request 0° using the existing calibration. A selected-joint zero command first shows current angle, target, and travel distance. It executes a bounded movement only after the path and relevant limit boundary are validated. J2/J3 zero lies at a cached range boundary, so a generic margin rule must not silently redefine zero. An all-joints-zero pose or upright pose requires its own validated sequence; neither belongs to the initial arrow-key test.

**Calibrate zero:** declare the current physical joint position to be zero. This changes the coordinate reference and requires correct mechanical alignment. It is not a movement command. Exclude zero-writing and encoder/calibration changes from the first release. [Joint calibration implementation][calibration].

**Enable/disable:** change whether motor drive is active. The application must distinguish accepting keyboard commands from motor power state. Disabling a raised arm can make it fall; quitting must not automatically disable it. Enable must be part of a controlled handover, with fresh position and known mode behavior, rather than an unconditional startup action.

**Software stop/reset:** the vendor describes electronic emergency stop as allowing a raised arm to descend with damping, while reset powers the arm off. Neither is a guaranteed rigid position hold. The exact behavior on S-V1.8-2 must be tested with the arm supported before assigning a terminal stop action. No automatic reset, fault clear, or re-enable on reconnect. [Enable, disable, stop, and reset documentation][stop-api].

Stopping key input, ceasing CAN transmission, holding a target, software emergency stop, and removing motor power are different operations. The program must name the action it took and never claim a disconnected arm has stopped without evidence. Physical emergency stopping remains necessary because software cannot send through a disconnected cable.

## 6. Proposed terminal experience

Use a foreground **C17 application with ncurses**, provisionally named `piperctl`. Dependencies are libc, Linux networking interfaces, and ncurses; no SDK or Python process runs underneath it. Start with a single process so controller lifetime and ownership are obvious. A C daemon with a local Unix socket can be added later if background monitoring becomes useful; boot-time motion control is outside this first release.

The following is proposed command-line syntax, not an implemented program:

```text
piperctl scan
piperctl monitor --arm piper-1
piperctl control --arm piper-1 --step-deg 0.25
```

`piper-1` would be a locally saved adapter association chosen after discovery. It is not an assumed factory serial or a hard-coded `can0` name. `control` opens in monitor state and requires an explicit takeover before motion.

| Input | Behavior |
| --- | --- |
| `1`–`6` | Select a joint; no movement. |
| Left / right arrow | Request one negative/positive position increment in degrees. |
| `:target 12.5` then Enter | Preview and submit an absolute angle for the selected joint, with units, range checks, and a maximum travel rule. Large moves require a separate deliberate action. |
| `:step 0.25` | Change step size within the configured test limits. |
| `:status` / `:diagnostics` | Explain measured state or the specific reason control is unavailable. |
| `:take-control` | Perform exclusive application handover and the validated mode/enable sequence. |
| `:monitor` | End keyboard control using the validated cessation procedure; continue reading feedback. |
| `:zero` | Proposed selected-joint zero preview; available only after zero-path validation. |
| Space / Escape | Cancel pending input immediately and invoke the validated stop procedure. Show whether it was transmitted and what feedback followed. |
| `q` / terminal closure | End control through the same validated procedure where possible, restore the terminal, and close resources. Do not silently disable motors. |

Numeric command entry takes precedence over joint-selection keys while the prompt is open. The display always shows the selected joint, actual position, target, error, limits, measured velocity/current, enabled/fault flags, and feedback age. It also shows adapter identity, link state, firmware, motion mode, and whether the program is monitoring or controlling.

A starting step of 0.25° is a proposal for commissioning, not a universally safe value. Speed, target lead, stop behavior, and acceptable position error must be established on the supported arm. Do not queue unlimited key repeat: allow at most a bounded outstanding increment and refuse another step when tracking error or feedback age exceeds the configured threshold. Ordinary terminals do not reliably provide key-up events, so holding an arrow must not mean unbounded velocity until a presumed release.

## 7. Discovery, connection ownership, and reconnection

### Identify the adapter and then the arm

Enumerate Linux CAN interfaces, link state, bitrate, driver, USB identity, and physical USB path using netlink and sysfs. Prefer a unique USB serial for saved identity; use a physical port path only as an explicitly weaker fallback. A USB adapter's identity is not the robot's identity: moving its CAN cable to another arm requires the operator to re-establish the association.

Listen for a consistent PiPer feedback set before declaring an arm found. A present USB device, an interface named `can0`, or one familiar CAN ID is insufficient. Begin with passive listening. Query firmware and supported configuration using a small allowlist of verified read requests; these transmit frames but must not change control mode or motor state.

Use the documented 1 Mbit/s classical CAN setup for this arm, after checking the actual interface configuration. If an interface is down or configured differently, explain the mismatch. Offer an explicit setup operation using narrowly scoped network-administration privileges; do not run the whole terminal as root or silently reset interfaces owned by another application. [Official CAN setup][can-setup], [SocketCAN][socketcan].

If several arms are visible, list them and require choosing the intended stable association. The application controls one arm, even while other adapters are plugged in. Treat multiple arms sharing one bus with colliding default IDs as unsupported until addressing is designed explicitly.

### Allow one motion controller

Before the later C motion tests, stop the existing dashboard's control service and ensure the tray or another client does not restart it. This handover is part of commissioning; the current planning work does not stop or change that service.

Use an advisory per-adapter lock for our own tools and inspect other known local controllers. SocketCAN sockets are not inherently exclusive: a lock cannot prevent unrelated software or another CAN node from transmitting. Unexpected control traffic is evidence to investigate, and the absence of observed traffic is not proof of exclusive ownership. There must be no automatic ownership fight or repeated forced mode changes.

### Recover to monitoring

```text
Absent → Interface found → Receiving → Identified → Monitor-ready
                                                   |
                                            explicit takeover
                                                   |
                                              Controlling
                                                   |
                                        loss, fault, or stop
                                                   |
                                          Control suspended
```

On USB removal, interface replacement, bus-off, stale critical feedback, or a control fault: reject new movements, discard pending commands, mark values stale, and record the reason. If communication still works, apply the validated stop procedure. If it does not, report that stop delivery is unavailable.

On reconnection, close obsolete sockets, resolve the saved USB identity to its current interface/index, and create a new connection generation. Require fresh firmware/status/joint data. Never replay the old target, resume a velocity, restore motor enable, or return to control automatically. The operator must take control again. Use bounded retry/backoff; expose every recovery attempt in diagnostics.

## 8. Diagnostics that distinguish failures

| Layer | Evidence to display | What it can establish |
| --- | --- | --- |
| USB | Presence, identity, enumeration/reconnect events when available. | Whether Linux currently sees the adapter. A disappearance alone does not identify hub, cable, power, or driver as the cause. |
| CAN interface | Name/index, UP state, bitrate, controller state, error/drop counters. | Whether the network interface is configured and whether the CAN controller reports errors. |
| CAN traffic | RX/TX counts, frame rates, accepted/rejected IDs and lengths, error frames, local send errors. | Whether frames are flowing. Local loopback/TX echo must not be mistaken for an arm reply. |
| PiPer feedback | Expected message coverage, per-field ages, firmware identification. | Whether coherent PiPer data is arriving on this adapter. |
| Actuator readiness | Joint/driver errors, enabled flags, control ownership and mode. | Whether the reported state permits the requested action. |
| Command result | Requested target, frames sent, acknowledgment where defined, measured response and elapsed time. | Separates transmission from acceptance and actual motion. |

Use monotonic timestamps for deadlines and wall time for operator logs. Bound log storage and record raw CAN ID, length, bytes, direction/echo metadata, decoded values, connection generation, and state transitions. CAN messages here do not provide a universal application sequence number; do not invent guaranteed request/reply matching where the protocol lacks it. Serialize queries where necessary and distinguish ambiguous replies.

Diagnostic messages should be concrete: “adapter present; CAN UP; no PiPer feedback for 2.1 s” is useful. “Hub broken” without supporting evidence is not. Likewise, changing the implementation language is not evidence that a previous failure was caused by Python.

## 9. Implementation outline after review

Create a separate repository in `piper-direct-c`; the only project artifact so far is this plan. Keep modules small and responsibilities explicit:

| Module | Purpose |
| --- | --- |
| Discovery | Netlink/sysfs enumeration, stable adapter associations, hotplug tracking. |
| CAN transport | Socket lifecycle, polling, filters, error frames, counters, and exact frame transmission. |
| PiPer protocol | Pure C encoders/decoders, units, firmware profiles, and a command allowlist. |
| Arm state | Per-field timestamps, validated snapshots, mode/driver state, and connection generations. |
| Control | Handover, targets, limits, input gating, fault/stop state, and completion checks. |
| Terminal and logs | ncurses display, explicit actions, bounded event/frame logging. |

Use one event loop for socket input, terminal input, and monotonic timers initially. UI rendering must not block freshness checks. Reject non-finite numeric input, overflows, invalid IDs, inappropriate frame formats, and unexpected message lengths before they reach control state. Encode fields explicitly, with message-specific signedness, units, and byte order; do not transmit native C structs or assume every message is eight bytes. For example, CPV commands are seven-byte payloads and some acknowledgments are one byte. [Protocol encoder/decoder][parser].

The initial protocol surface comprises joint/status/motor feedback, firmware and limit queries, normal joint targets, verified mode/enable actions, and the validated stop procedure. CPV position follows as a separate milestone. Persistent calibration, tuning writes, firmware updates, Cartesian planning, MIT torque mode, automatic fault clearing, and generic raw-command entry are excluded initially.

No startup code should change installation orientation, payload, gains, calibration, or leader/follower role. Configuration with mixed read/write fields needs explicit zeroed or documented no-change fields, verified against byte fixtures. Readback is required where supported; an acknowledgment alone does not prove the intended physical result.

## 10. Verification and delivery milestones

| Milestone | Deliverable | Acceptance evidence |
| --- | --- | --- |
| A. Offline protocol core | C build and test fixtures derived from pinned public protocol definitions. | Signed/unit conversions, limits, malformed frames, firmware routing, and byte encodings pass without hardware or an SDK runtime. |
| B. Monitor and discovery | Terminal shows the selected arm and six fresh joint states. | Passive mode emits no robot commands; explicit information queries emit only their allowlisted frames. Unknown data stays unknown. Adapter renaming and unplug/replug recover without motion. |
| C. Controlled MoveJ steps | `1`–`6`, arrows, absolute target input, configurable step, and diagnostics. | Explicit ownership handover; small movement matches selected joint and sign; other targets remain fixed; range/stale/fault conditions reject motion; stop behavior is observed on the supported arm. |
| D. Individual CPV position | Selected-joint addressing without sending a normal six-target MoveJ vector. | Confirmed firmware compatibility, entry/exit behavior, first-write semantics, configured speed/acceleration, sign/scale, response, and continued support of unselected joints. No hidden flash writes. |
| E. Zero movement | Deliberate selected-joint zero command, if requested after initial tests. | Mechanically verified zero reference and a validated travel path; feedback establishes completion; calibration is unchanged. |

Before physical movement, use Linux `vcan` to test transport and simulated feedback, including incomplete joint sets, stale state, socket/interface loss, partial sends, full queues, unexpected control frames, and reconnection generations. Exercise the same event loop and command gate used by the real terminal. A virtual bus verifies software behavior; it cannot verify motor response or USB hardware recovery. [SocketCAN virtual interfaces][socketcan].

Physical commissioning then uses one identified arm, an unobstructed small-motion range, and a supported pose for stop/disable characterization. Establish mode ownership and a no-jump enable sequence before the first increment. Start with one joint and a small approved increment, inspect feedback and physical direction, then repeat for the other joints. Do not begin with a full sweep, all-zero pose, or torque mode.

Test command loss and reconnection explicitly. A killed process or unplugged USB cable cannot execute a cleanup handler or deliver a software stop. Measure the arm's actual behavior and verify an embedded command timeout if one is available; until then, document that a previously accepted position target may finish after the host disappears. Continuous velocity and torque control remain deferred.

The first useful release is complete when one arm can be identified, monitored, deliberately taken under control, and moved in small selected-joint position steps with clear failure reasons and no motion replay after reconnection. CPV completion is a separate, observable result; it must not be claimed merely because the keyboard controls one joint through MoveJ.

## 11. Decisions for review

The proposed starting scope is **one arm, six position controls, C terminal, raw SocketCAN, explicit takeover, and strong connection diagnostics**. Start with a read-only build, prove the standard joint-position path, then validate CPV position. This provides both a reference behavior and a route toward the requested individual-actuator interface.

Before implementation proceeds, the main choice is whether to follow that staged path or prioritize CPV position immediately after read-only diagnostics. Either choice keeps velocity/torque control, persistent calibration, gripper motion, GUI/daemon work, and firmware changes outside the first motion test.

No claim is made yet that the internal microcontroller can be bypassed, that the reported angles are independently accurate, or that any USB failure has been diagnosed. Those questions require different evidence. The planned logs and controlled tests are intended to supply it.

## Reference versions

The plan was checked against official AgileX source at fixed revisions so later SDK changes do not silently change its basis:

- `agilexrobotics/piper_sdk`: [`c9e8a28174e71eeaac448593cb65f8ab258a92fe`](https://github.com/agilexrobotics/piper_sdk/tree/c9e8a28174e71eeaac448593cb65f8ab258a92fe).
- `agilexrobotics/pyAgxArm`: [`841a625f5f4920e776f20b934eb13048b747e6d0`](https://github.com/agilexrobotics/pyAgxArm/tree/841a625f5f4920e776f20b934eb13048b747e6d0).
- Linux kernel [SocketCAN documentation][socketcan].

Source code was read as a protocol reference. No SDK was installed or executed for this plan. Arm-specific version/limit observations came from a read-only request to the existing dashboard; they must be freshly verified during C commissioning. Device serials and local logs are omitted from this document.

[legacy-protocol]: https://github.com/agilexrobotics/piper_sdk/blob/c9e8a28174e71eeaac448593cb65f8ab258a92fe/piper_sdk/protocol/protocol_v2/piper_protocol_v2.py
[joint-feedback]: https://github.com/agilexrobotics/piper_sdk/blob/c9e8a28174e71eeaac448593cb65f8ab258a92fe/piper_sdk/piper_msgs/msg_v2/feedback/arm_feedback_joint_states.py
[driver]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/pyAgxArm/protocols/can_protocol/drivers/piper/default/driver.py
[parser]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/pyAgxArm/protocols/can_protocol/drivers/piper/default/parser.py
[high-feedback]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/pyAgxArm/protocols/can_protocol/msgs/piper/default/feedback/arm_feedback_high_spd.py
[low-feedback]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/pyAgxArm/protocols/can_protocol/msgs/piper/default/feedback/arm_feedback_low_spd.py
[cpv-messages]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/pyAgxArm/protocols/can_protocol/msgs/piper/default/transmit/arm_cpv_settings_and_queries.py
[constants]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/pyAgxArm/api/constants.py
[firmware]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/docs/piper/firmware_reference.md
[api]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/docs/piper/piper_api.md
[version-drivers]: https://github.com/agilexrobotics/pyAgxArm/tree/841a625f5f4920e776f20b934eb13048b747e6d0/pyAgxArm/protocols/can_protocol/drivers/piper/versions
[cpv-driver]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/pyAgxArm/protocols/can_protocol/drivers/piper/default/driver.py#L2533
[cpv-api]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/docs/piper/piper_api.md#L2026
[calibration]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/pyAgxArm/protocols/can_protocol/drivers/piper/default/driver.py#L1791
[stop-api]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/docs/piper/piper_api.md#L1586
[gripper-feedback]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/pyAgxArm/protocols/can_protocol/msgs/effector/agx_gripper/default/feedback/arm_feedback_gripper.py
[gripper-driver]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/pyAgxArm/protocols/can_protocol/drivers/effector/agx_gripper/default/driver.py
[can-setup]: https://github.com/agilexrobotics/pyAgxArm/blob/841a625f5f4920e776f20b934eb13048b747e6d0/docs/can_user.md
[socketcan]: https://docs.kernel.org/networking/can.html

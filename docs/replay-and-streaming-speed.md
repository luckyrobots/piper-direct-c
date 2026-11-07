# Taught replay and streamed simulation motion

The physical teach button replays a trajectory inside the arm controller. Our
plugin instead streams a simulation's position targets through the managed
publisher, native reference shaper and CAN MoveJ controller. Those are different
execution paths. We have recorded the encoder results of taught replay, but not
the firmware's internal trajectory algorithm or update frequency.

## What the existing captures show

The two observed taught replays took approximately 8.33 and 7.24 seconds. The
200 ms angle fits peaked at 114.9 degrees/second for J2, 121.1 for J3 and 213.2
for J5. These were different motions from the tube task, so their whole-cycle
durations are not directly comparable. The measurements also do not replace
the reported limits used to admit streamed commands.

The verified tube cycle at the saved 90% setting took 36.63 seconds from native
following admission to permanent zero, including 31.04 seconds from the first
quarter-degree excursion. The same 200 ms fit method, with a lower observation
rate, found approximately 21.4 degrees/second for J2, 21.0 for J3 and 26.9 for
J5. Maximum sampled transmitted-reference tracking error was 1.453 degrees.

Evidence: workspace outputs `native-replay-speed-summary-20261103.json` and
`normal-grasp-final-90-verification-20261105.json`. The latter uses roughly
10 Hz observations, so brief peaks can be missed.

## Remaining differences in our implementation

* `managed/Piper.Plugin/OrderedSimulationPath.cs`, `OrderedSimulationPhase`,
  retains every source waypoint and builds C1 monotone cubic curves. Velocity
  is continuous; acceleration can jump at knots. Arm phases stop at jaw and
  verification barriers.
* `managed/Piper.Plugin/OrderedSimulationExecution.cs`, `Step`, waits for
  stationary encoders and 100 ms of distinct settled feedback at each phase
  endpoint. The current native stationary condition itself requires 400 ms.
* `managed/Piper.Plugin/QueuedSimulationFollowing.cs`, `PublishOrderedTargets`,
  previously waited for both arm and Pika acknowledgements and then slept
  another 30 ms. A sampled sequence estimate from the normal run gives a
  typical publication period around 36.2 ms, rather than 30 ms. The new
  `SimulationPublishCadence` schedules a monotonic 30 ms deadline grid, counts
  acknowledgement time inside that period, and skips expired deadlines rather
  than issuing catch-up writes. It changes no position, mode or protection
  threshold. Its physical benefit still needs a new comparison capture.
* `src/controller.c`, `trajectory_reference_step`, applies another independent
  velocity/acceleration shaper at native 20–50 ms intervals. It receives a
  destination, without the upstream path's desired velocity or acceleration.
  It brakes toward both that changing destination and a wall 2 degrees ahead
  of the measured joint. With the current reported 5 rad/s² acceleration and
  millidegree reserve, the wall's braking calculation allows about 28.5
  degrees/second at a 20 ms tick when reference and measurement coincide,
  falling as the reference gets ahead. This is a substantial practical speed
  restriction even when the panel says 100%. The independent 3-degree tracking
  fault remains separate.
* `src/controller.c`, `send_pose`, sends the same mode/speed control along with
  each changed six-joint position. `src/protocol.c`, `piper_mode`, selects
  ordinary CAN MoveJ (`0x151`: control 1, motion 1, MIT/pass-through byte 0).
  The firmware smooths these received position commands too. Short rolling
  destinations, two planners and host timing jitter can therefore contribute
  to the feel; we have not isolated their individual physical contributions.

The 2-degree wall should not simply be enlarged to make a speed number higher.
That would change the verified tracking envelope and require a different
trajectory admission/tracking design. A smoothness improvement should be
checked with a repeatable path, command/encoder timing, velocity and acceleration
traces, and the normal hold/obstruction behavior.

## MoveJ endpoints versus JS / high-follow mode

AgileX documents ordinary `move_j` as position-velocity control whose lower
layer smooths received messages. Its `move_js` follower mode instead enables
MIT pass-through with no lower-layer smoothing or trajectory planning. It
overwrites the prior target on consecutive commands. Calling it "smoother
MoveJ" would be incorrect. The existing saved taught-replay observations do not
establish which internal servo mode that replay uses.

Sending one ordinary MoveJ endpoint for a complete arm phase would let the
firmware plan toward a longer destination. It could feel smoother and move
faster, but would discard the intervening simulated Cartesian path, obstacle
clearances and approach waypoints. It is therefore not a drop-in replacement
for the current exact waypoint sequence. A qualified endpoint strategy would
need its own collision-checked segments and separately verified execution.

Primary references, pinned to the inspected vendor revision:

* [PiPER control modes and move_js semantics](https://github.com/agilexrobotics/pyAgxArm/blob/d90e234ba8e408cfa964b38aa9120b3131fc9f85/docs/piper/piper_api.md)
* [PiPER default driver: move_j, move_js and set_motion_mode](https://github.com/agilexrobotics/pyAgxArm/blob/d90e234ba8e408cfa964b38aa9120b3131fc9f85/pyAgxArm/protocols/can_protocol/drivers/piper/default/driver.py)

### Laboratory streamed-position transport — disabled after physical qualification

**Physical qualification failed. Shipping builds disable this transport at
compile time.** There is no runtime preference, environment variable or API
request that enables it in the normal build. Ordinary position-velocity
following remains the supported execution path.

In the 2026-11-05 controlled comparison, all three ordinary PV wrist cycles
completed. JS then failed during its initial stationary zero hold, before any
J6 motion: every transmitted reference remained zero, but measured J5 moved
past 1.3 degrees in roughly 0.1 seconds. The fixed-other-joint guard retired
following. A subsequent measured hold could not be encoded because J3 had
drifted to +0.215 degrees, beyond its reported 0-degree upper limit; the
controller requested STOP. No physical JS cycle was qualified. This is
consistent with the vendor's documented stiffness/force difference, but the
capture does not identify the firmware's internal gain or gravity behavior.
No gain, torque or protection limit was changed to compensate.

The local raw qualification receipt is
`outputs/lucky-engine/j6-pv-js-comparison-20261105.json`. It records four JS
stationary-hold observations, no moving JS leg, and the scoped hold rejection.
Its receipt-time measurements do not establish firmware servo dynamics.

`PIPER_ENABLE_EXPERIMENTAL_STREAMED_POSITION` defaults to **0** in
`src/protocol.h`. In that build both JS capabilities and per-device support
are false, the API rejects a JS request before action intent, and the actual
CAN allowlist rejects its mode batch. The normal Windows build does not set
the macro. Only explicitly compiled, hardware-disabled laboratory fixtures
set it to 1 to retain protocol/validator regression coverage. The following
contract describes that laboratory code, not a usable shipping control mode.

Native admission is explicitly scoped to:

```json
{
  "action": "follow-start",
  "trajectory": true,
  "trajectory_transport": "streamed-position"
}
```

The remaining existing identity, owner, generation, workspace, limits and
motor-state fields are still required. A request without this field keeps
ordinary position-velocity behavior. The field is rejected on manual commands,
ordinary non-trajectory following, updates and non-admission actions. Only
reviewed `H-V1.2-1` with exact `S-V1.9-0` or `S-V1.9-1` firmware is admitted.
Unknown revisions do not inherit support.

The backend must also advertise
`streamed_position_wire_validation_supported`. Before recording action intent
or admitting following, it runs the initial four-frame transaction through
the **same production `can_bus_batch_allowed` validator** used by
`can_bus_send`. JS is admitted only as an ordered complete
`0x151/0x155/0x156/0x157` batch, with reviewed firmware and zero reserved bytes.
A wire-validation rejection returns before any action or motor command starts.
The admission acknowledgement includes `trajectory_transport_validated=true`;
the plugin and comparison harness require that proof. The shipping capability
is false, so they reject JS before any physical admission.

An admitted streamed-position trajectory sends CAN `0x151` with control=1,
motion=1 and byte3=`0xAD`; joint angles remain the same millidegree
`0x155`–`0x157` messages. No impedance/torque/gain command is introduced.
The wire-pose cache includes this mode byte. The host percentage still shapes
the reference clock and speed even though the firmware's JS mode does not
apply the ordinary speed ratio.

All current reported velocity/acceleration bounds, 2-degree soft lead wall,
3-degree hard tracking fault, fresh-feedback/owner/generation checks and
0.5-second obstruction behavior remain. This experiment can remove the second
firmware planner; it does not remove the remaining host lead-wall speed cap or
add a jerk-qualified profile.

Follow-end, input expiry and verified software-boundary cancellation issue
the existing measured-pose hold in the **same JS transport**, including while
the arm is moving. The idle controller does not repeatedly switch modes. A
subsequent ordinary pose action requires the existing measured stationary gate
before admitting position-velocity control again. Genuine faults and failed
holds still use STOP. The transport field in state records the requested
native transport; normal `0x2A1` MoveJ feedback does not independently prove
the firmware's MIT/pass-through flag.

The managed plugin pins the selected profile's `SimulationTransport` at
preparation, validates native capability and firmware, propagates the explicit
choice on follow-start, and requires its exact acknowledgement before
admitting Pika or releasing Play. Its default is `position-velocity`; saved
profiles lacking the field remain unchanged. Normal controls, homing and
matching do not opt into JS. The physical qualification above failed, and
there are no further physical JS comparisons or pickup attempts in this
implementation.

## Where to tune speed

1. **Physical execution:** stop following, adjust the plugin's **Speed** slider,
   then prepare Follow simulation again. `Preferences.Speed` is a per-robot
   0–100 percentage in `managed/Piper.Plugin/PipersPlugin.cs` and
   `RobotProfiles.cs`. It is pinned at preparation in
   `QueuedSimulationFollowing.PrepareOrderedSource`. Zero prevents admission.
   A 100% setting requests the reported ceiling; the native tracking shaper can
   still be the bottleneck. Do not edit decoded caps or protection thresholds
   to tune ordinary speed.
2. **Simulation choreography:** select the scene's `WoodFillerDemonstrator`
   script. `TravelSpeed` is metres/second, `VerticalSpeed` is metres/second,
   and `AngularSpeed` is radians/second. `DurationScale=1` uses those configured
   speeds; values above 1 slow the demonstration. Values below 1 are rejected.
   The source file is
   `Assets/Scripts/Client/Source/WoodFillerDemonstrator.cs`; `AppendMove` computes
   movement duration from distance and angle. These are simulation speeds,
   not permission to exceed the real controller's admitted limits.

Next work for matching taught replay is native, timed multi-sample trajectory
execution with a qualified smooth motion profile, rather than increasing the
percentage above 100 or assuming that a faster host loop alone solves it.

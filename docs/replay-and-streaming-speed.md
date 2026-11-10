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
* `managed/Piper.Plugin/GroupSimulationFollowingRuntime.cs`,
  `PublishGroupTargets`, publishes ordered targets. Its predecessor
  (`QueuedSimulationFollowing.cs`, since removed) waited for both arm and Pika acknowledgements and then slept
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

### Streamed-position transport — removed after failed physical qualification

**The experimental streamed-position (JS) transport has been deleted.** The
native `PIPER_ENABLE_EXPERIMENTAL_STREAMED_POSITION` code, its laboratory
fixtures and the plugin's streamed branches no longer exist. Ordinary
position-velocity following is the only execution path.

The 2026-11-05 controlled comparison led to that decision. All three
ordinary PV wrist cycles completed. JS then failed during its initial
stationary zero hold, before any J6 motion: every transmitted reference
remained zero, but measured J5 moved past 1.3 degrees in roughly 0.1 seconds.
The fixed-other-joint guard retired following. A subsequent measured hold
could not be encoded because J3 had drifted to +0.215 degrees, beyond its
reported 0-degree upper limit; the controller requested STOP. No physical JS
cycle was qualified. This is consistent with the vendor's documented
stiffness/force difference, but the capture does not identify the firmware's
internal gain or gravity behavior. No gain, torque or protection limit was
changed to compensate.

The local raw qualification receipt is
`outputs/lucky-engine/j6-pv-js-comparison-20261105.json`. It records four JS
stationary-hold observations, no moving JS leg, and the scoped hold rejection.
Its receipt-time measurements do not establish firmware servo dynamics.

The shared simulation follower accepts only the `position-velocity`
transport. Profiles lacking `simulationTransport` default to it. A new JS
attempt would need a new implementation and a new physical qualification.

## Where to tune speed

1. **Physical execution:** stop following, adjust the plugin's **Speed** slider,
   then prepare Follow simulation again. `Preferences.Speed` is a per-robot
   0–100 percentage in `managed/Piper.Plugin/PipersPlugin.cs` and
   `RobotProfiles.cs`. It is pinned at preparation in
   the captured `GroupLane.Profile` and `RunGroupSimulation` execution. Zero prevents admission.
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

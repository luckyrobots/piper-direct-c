# Obstruction detection and recovery

Joint MoveJ, range-entry and synchronized/cycle motion monitor each joint independently. When a joint has more than the selected tolerance left to travel and its command leads the measurement by at least `max(0.05°, selected tolerance)`, the backend requests STOP after 0.5 seconds without at least 0.01° of progress toward its target. Tracking inside the accepted tolerance does not count as an obstruction. This avoids false alarms on short-travel joints during slow synchronized moves (the observed all-zero event had only 0.111° J6 tracking error at 0.5° tolerance). Other joints cannot conceal a stalled joint. Monitoring resets at each action and cycle leg. Existing driver-fault, stale-feedback, tracking-error and heartbeat checks remain. The separate standard CAN gripper retains its existing progress checks; Pika has its own serial controller.

This is a position-feedback stall heuristic, not a contact sensor. It cannot guarantee a stop within 0.5 seconds of physical contact, detect every creeping/slipping obstruction, or guarantee the delivery or braking time of a software STOP. Do not validate it by putting a hand in the robot's path. Use injected frozen feedback, then an appropriate controlled hardware fixture if physical validation is needed.

The first stop records wall-clock time, reason, joint number where detected, request ID, selected speed, measured/commanded/final angles, current when fresh, driver fault bits, controller status and per-joint progress age. Later STOP commands cannot overwrite that event until recovery clears the active incident. The event survives process restart. Old stops without evidence are identified as such.

The web dashboard displays the event and live controller state, support/clear/reset/verify steps, teaching-state restrictions and range issues. In the native Pipers panel, a telemetry-derived recovery plan selects verification without a reset when already healthy, or one explicit **Clear & support arm — recover** action for a controller stop. Reset can remove holding torque. Its completion is verified automatically; neither reset nor verification retries the interrupted move or enables motors. Unknown/driver faults, missing feedback and active teaching withhold reset. Cameras remain available for operator inspection; their images do not authorize autonomous resets or clearance moves. Measured scene tracking continues through recovery, while scene-to-hardware commands are disarmed until an explicit Enable action. The dashboard never rewrites calibration to resolve an obstruction.

## Offline validation

- Detector tests: frozen joint threshold at 500 ms, independent joint progress, slow movement in both directions, settled/uncommanded joints and small oscillations.
- Controller simulation: frozen fresh feedback queues STOP in 500–650 ms including command-lead startup; original event survives another STOP and controller restart with no motion replay.
- Existing control-guard, startup/recovery and synchronized/cycle tests passed.
- Native core builds treat warnings as errors. JavaScript syntax and shared-prompt regressions cover the incident UI.
- These fixtures do not validate physical contact sensing or guarantee delivery/braking time on hardware. Physical validation requires a controlled fixture and independent assessment.

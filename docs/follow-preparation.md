# Shared follower preparation

The sidebar evaluates every checked robot, including robots whose controls are
not currently visible. It shows the robot's name and the next required action:
for example, **Piper 1: Use Resume controls** and **Piper 1: Use Enable robot to
enable this robot's Pika gripper**. Selecting a checkbox still only saves a
preference. It does not switch control modes, enter Play or send a target.

Follow remains clickable when robots are selected. A temporary encoder-stillness
or `can_return` change does not disable this explicit action. Clicking it while
unready displays the same preparation report without arming Play or sending
arm/gripper commands. The failed attempt stays beside Follow until another
explicit attempt or a membership change. A later runtime failure also retains
its original cause there after cleanup.

`FollowPreparationReport.Inspect` is a public, pure boundary used by UI and
admission. Its inputs are captured profiles, one telemetry receipt, exact
backend/connection approvals, scene descriptions and an explicit monotonic time.
It never selects a device, projects the receipt, reads files or issues commands.
Approval scope includes backend, CAN identity/generation, scene arm/prefix and
Pika identity/distribution. It checks the complete group's assignments,
teaching/recovery/power/health/configuration and Pika control/readiness. Scene
discovery and subsequent native/engine admission still validate live identities.
This report does not replace firmware handoff, lease, source expiry or endpoint
guards, and it never interprets a failed check as permission to reset or enable.

Only `position-velocity` is qualified for this follower. A profile saved with
`streamed-position` or another transport is named in the same sidebar/admission
report and prevents arming. Its saved choice is not silently ignored or rewritten;
explicitly change that profile's `simulationTransport` to `position-velocity`
before retrying. There is no experimental transport enable action here, and
backend capability advertisements cannot override this restriction.

The production call graph is:

```text
HandleEventAsync -> UI actor -> HandleEventCoreAsync
  -> GroupFollowWorkspaceEvent -> GroupFollowEvent
       -> GroupPreparation -> FollowPreparationReport.Inspect
       -> read-only engine capabilities / scene discovery
       -> GroupPreparation with the new scene and one current receipt
       -> capture immutable lanes -> live GroupHardware checks
       -> engine arm-next-play (Edit remains Edit)
       -> RunGroupSimulation after the operator presses Play

GroupFollowControls / AddGroupFollowPatches
  -> GroupPreparation -> FollowPreparationReport.Inspect
  -> named readiness or retained failure beside Follow
```

SDK callbacks, the UI actor and background runtime workers are asynchronous entry
points rather than private single-use helpers to inline. Keeping these boundaries
explicit permits immediate STOP/cancel and orderly worker draining. One existing
coordinator now runs both one and multiple selected robots; the parallel legacy
single-robot executor has been removed. Viewport dragging remains a separate
input mode because its gesture/commit contract differs from an ordered episode.

The coordinator owns arming, user Play, first-frame matching, native admission,
source publication and cleanup as one worker lifetime. A late engine or native
acknowledgement cannot release the captured identity or let a replacement group
start before all requests retire and final holds finish. STOP also retires
read-only discovery before a session has been allocated. Measured Edit feedback
keeps the current jaws visible while merely Armed; runtime takeover ends that
Edit lease. Native control guards and source deadlines are unchanged.

Every participant retains bounded publication timings and an independently
scoped first-failure diagnostic, including negative Pika acknowledgements and
ordered-phase endpoint evidence. Independent cleanup attempts include their
evidence lookup, so a malformed arm or gripper inventory cannot prevent holds
for a healthy partner or the final engine cancellation/pause. A fresh, exact
post-admission idle receipt can prove an arm already retired; an idle controller
does not receive an unsupported hold request.

Offline regressions use the actual public UI event path, simulated engine RPCs
and fake arm/Pika calls. They cover an unready nonselected partner, teaching ended
with a disabled gripper, STOP preservation, retrying once in Edit, public profile
navigation during following, stable action clickability, and immediate group
cancel while the normal command semaphore is occupied. The unified suite also
covers canonical and prefixed one-participant tracks, delayed engine/native
acknowledgements, lost acknowledgements, discovery cancellation, malformed
partner evidence, source expiry/model/cursor rejection, per-channel diagnostics,
and completing release/home after a missed grasp.

# Using the Pipers workspace

Choose a robot in the left bar. Its joint controls, Pika gripper and two wrist
cameras appear together. The bench camera stays in the left bar because every
robot shares it. Connection details and configuration live under the single
**Settings** gear above the robot list. Hover over an **(i)** icon for explanations.

## Connect and use one robot

1. Open **Settings → Robots & devices**. Give the robot a name and choose its
   exact CAN adapter, Pika USB gripper and individual **Simulation arm**. Each
   real robot has one assignment, such as **Piper 2 → Sim arm 2**. Two arms may
   share one physics model internally; their joints, cameras and assignments
   stay separate. An arm assigned to another profile cannot be selected twice.
2. Open **Settings → Cameras**, select that robot and assign its Pika fisheye and
   D405 by device identity. The **Bench · shared** tab configures the common
   bench camera once. If a Pika says setup is needed, use **Set up USB gripper**
   beside its assignment and accept Windows' one-time administrator prompt.
3. Return to **Controls**. Click **Enable robot**, then confirm the workspace is
   clear. This establishes the selected arm's controls and enables its assigned
   Pika. Already-enabled arm motors are recognized; a second enable command is
   not needed merely to acquire the plugin's controls.
4. Use the joint or gripper sliders, the −/+ buttons, or **Drag joints**. Joint
   sliders select absolute angles. **Jog step** changes the −/+ increment.
   **Speed** is a percentage of this robot's reported limits; the displayed °/s
   value is a target ceiling, not a claim about its measured instantaneous speed.

Names, assignments, speed, jog step, tolerance, camera selections and preview
preferences are saved. Switching the visible robot keeps independent Pika
connections alive. Outside grouped following, a previous manual movement is held
or ended before another arm is selected. While a follower group owns the robots,
switching the visible profile changes the controls/cameras you inspect; it does
not redirect, restart or end either participant's ordered movement.

## Follow the simulation

Enable each participating robot and its assigned gripper first. The main robot
button then says **Enabled**; it does not start or prepare a simulation.

1. For each robot, check **Follow simulation** in its controls if it should join
   playback. Checking a box only saves the selection. It does not move a robot,
   pause the editor or enter Play.
2. Click the single **Follow simulation** button in the left sidebar. It arms the
   checked group and leaves the editor in Edit mode. Membership and device
   assignments stay fixed until you stop the group.
3. With the physical workspace clear, press the editor's **Play** button once.
   The engine pauses at the episode's first frame. The plugin slowly matches
   every selected real arm and gripper to its simulated counterpart, waits for
   fresh settled feedback and independent control acknowledgements, then starts
   the shared episode. Read the sidebar status if matching is still in progress.

Both real robots can follow their distinct arms in the same compiled scene world.
The current grouped follower requires those bindings to share one scene-world
entity, model revision and Play run. It rejects unrelated worlds or duplicate
CAN, gripper or scene-arm assignments. It also requires an updated engine with
`robotRuntimeTracksSupported` and a backend with `multi_device_lanes_supported`;
older versions show an update message before any first-pose movement.

The source retains both arm and jaw command tracks on one physics cursor. A
faster physical arm holds at each shared phase boundary until its partner also
arrives. This keeps the second robot's centre pickup behind the first robot's
physical delivery. The next episode waits for every participating arm and gripper
to finish; one robot cannot acknowledge the whole episode on its own. A missed
grasp may finish the existing home path, then holds the group without releasing
another episode. These checks establish ordered playback, not proof of a grasp.

The sidebar button becomes **Stop following** while armed or running. It ends
the group, pauses the simulation and independently requests a hold for every
participating arm and Pika. A real fault, lost feedback, changed device identity
or source interruption also retires the whole group. These actions do not power
off the robots or start a return to zero. The red
**STOP** requests the controller's stop and can remove holding torque; **Disable**
releases motor power. These actions have different effects even though they are
kept close to the main control.

**Show real robot ghosts** displays translucent measured arm and jaw poses in
both Edit and Play. The solid robot remains the simulated leader. The checkbox
only changes visibility: it does not enable motors, start following or change
the scene's physics. Ghosts are excluded from simulated camera recordings.
They require fresh matching arm and gripper feedback; an expired overlay is
hidden rather than presented as a current physical pose.

The measured ghosts continue showing each robot independently while the group
runs. The simulation may lead the physical motion; the ghosts show fresh measured
poses, rather than a prediction that the followers have already reached a target.

## Teaching and a stopped movement

The main action follows the controller's actual state:

| What the workspace shows | What to do |
| --- | --- |
| Waiting for feedback | Let the assigned connection recover. Old movement does not resume automatically. |
| Disabled / Enable robot | Click the main action and complete its workspace-clear confirmation. |
| Hand guiding | Support and guide the arm while the physical button is solid green. Single-click it again to finish. |
| Onboard playback | The physical controller owns playback. Let it finish or stop it through its controls. |
| Resume controls | Finish the supported return from teaching to CAN; the same-pose handoff discards old targets. |
| Ready / Enabled | Select its follower checkbox, then use the sidebar Follow button and editor Play. |
| Group armed / press Play | Press editor Play once; all selected first poses are matched before movement begins. |
| Following / Stop following in sidebar | End the group and hold every participating arm and gripper. |
| Stopped / Recover robot | Read the incident and use the recovery instructions for that stop. |

For a supported software obstruction, click **Recover robot**, single-click the
physical arm button for solid green hand guiding, move clear of the obstruction,
then single-click again and hold still. After the backend verifies the observed
button cycle, healthy feedback, joint ranges and stationary pose, it restores
CAN holding at that measured pose. It does not home, replay the interrupted
movement, change encoder zero or change motor power during this handoff.

For a latched controller STOP, the main recovery card shows its recorded cause.
Rest or support the arm, then click **Arm supported — recover**. This acknowledges
that holding torque will disappear. The plugin disables the motors, waits for
fresh all-off/stationary evidence, clears STOP once, verifies recovery, and
restores holding only at a valid measured pose. It does not send a destination.
If a joint is out of range, the card shows its angle and permitted range; hand-guide
it back using the physical button, turn the green light off, and use the shown
holding action. Cancel or Disable retires the recovery worker and old targets.

Drive faults, missing feedback and unsupported firmware retain their explicit
blockers. The recovery card and **Settings → Diagnostics** show why a command is
withheld. The plugin does not infer a clear physical path from a camera preview.

Onboard arm teaching and USB Pika teaching are separate mechanisms. The Pika
teaching controls handle its hand-moved jaw recording; the physical arm's saved
playback alone does not prove that USB gripper motion was recorded.

## Settings, calibration and recording

**Settings → Robots & devices** holds robot names, arm/gripper connections, scene
bindings and removal. **Cameras** holds physical devices, simulation counterparts,
preview controls and lens calibration. **Bench & calibration** holds the common
desk reference and the selected arm's calibration. **Diagnostics** holds detailed
controller state, recovery tools and the gripper's closed display reference.

The real and simulated camera previews use the selected profile's identities
and scene ancestry. Each view shows **REAL** above **SIM**; enlarge it to compare.
D405 **Color** and **Depth** select different stream types. Factory intrinsics,
fisheye lens calibration and camera mounting alignment are distinct steps:
correct routing does not establish pixel-perfect images or synchronized exposure.

Desk calibration records the jaw-tip midpoint at measured physical marks. Point
selection and viewport markers are visual guides; they never move the real arm
to a mark. The primary arm's alignment does not independently calibrate an
opposite arm. Bench alignment editing is currently unavailable for a namespaced
arm inside a compound model because moving its root would move both arms.
Joint following and camera previews remain available for that branch; verify
its physical mounting and tool alignment before using full pickup trajectories.

The **Recording** page summarizes calibration and stream checks. Its pending
setup indicator does not start a dataset recording. Use Lucky Engine's recorder
for the simulation-only dataset workflow. Real camera exposure timing,
camera-to-joint timing and sim-to-real task agreement must be validated before
claiming that exported actuator commands reproduce a physical task.

Ordinary settings inspection and changing the visible robot can remain open
during a following session. Changing robot/arm/gripper assignments or entering
calibration is blocked while the group owns those controls; stop following first.

**Settings → Diagnostics → Restart backend** reopens the controller connections.
Stop following first and empty every Pika gripper, then confirm **Grippers empty
— restart**. Restart releases Pika holding torque, preserves arm power and saved
STOP/recovery records, and starts a fresh monitoring session without replaying
targets. Reconnection happens automatically. A stopped arm still needs the
recovery flow above; restarting the server does not clear its fault.

Disable can preempt a pending UI action. A failed command stays visible at the
top of the workspace; a timed-out action is cancelled rather than replayed later.

If one USB-CAN connection fails, automatic reconnect reopens only that adapter.
The other arms keep their connections, firmware and limit readings. Robot
selection and Settings remain available while a selected arm has stale feedback;
movement still requires fresh readings from the newly selected arm.

Ordered simulation phases wait for measured arrival and a corroborated quiet
pose before the next arm or jaw event. Driver velocity quantization alone must
not trap an arm that has reached its endpoint. Position and raw encoder drift,
stale feedback, faults, and endpoint error still prevent phase completion.
If following fails, the first cause remains visible above the controls even
after cleanup has returned the hardware to a normal holding state.

A USB transfer timeout is different from a controller STOP. For a persistent
adapter timeout, reconnect only the affected adapter's USB cable, leaving robot
power and CAN connected. The plugin shows the affected robot and transport error;
it discards old targets and requires a fresh Enable action after reconnection.

For backend routing and Windows USB setup details, see
[Multiple Pika grippers](multiple-pika-grippers.md).

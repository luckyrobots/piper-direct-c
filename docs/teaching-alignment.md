# Hand guidance, return to CAN and alignment observations

Pipers detects the physical controller's teaching state separately from faults.
While hand guidance or onboard playback owns the arm, the measured scene link
continues and plugin arm targets are disarmed. The independent Pika connection
can remain active. Old movements and input gestures are discarded; they are not
resumed when teaching ends.

## Physical teaching and control handoff

The robot's button starts/stops hand-guided recording with a single press. Solid
green indicates recording; flashing green indicates playback. A double press can
replay the onboard trajectory. Starting another onboard recording replaces the
previous one. Pipers observes these states; it does not send an unsupported
software command to enter button teaching or start playback.

For a controller that remains in teaching mode after recording ends:

1. While hand guiding, bring the physical arm to its zero pose. The panel shows
   all six live angles. If teaching has already ended, follow the button guide
   before repositioning; do not force a powered joint.
2. End teaching so its indicator is off. Support the arm and keep the workspace
   clear during the mode change; standby holding behavior is not guaranteed.
3. Select **Return to CAN control**, then confirm the physical zero pose, light
   off, support and clear workspace in its single confirmation.
4. Pipers requests standby, waits for fresh acknowledgement, then requests CAN
   MoveJ and verifies its acknowledgement. It sends no reset, homing, motor-power
   or joint-position target. A missing acknowledgement is reported without an
   automatic reset/retry. A new command is needed after completion.

The handoff guard requires fresh healthy feedback, a stationary arm, consistent
motor state, current configuration and all joints within 1 degree of reported
zero and their command limits. The 1-degree bound is an application guard, not
a vendor-specified calibration tolerance. Physical zero confirmation is still
required. If feedback already returns to healthy normal control on the same
connection, prior UI approval can be restored without transmitting enable or
motion. Disabled motors still require the normal explicit Enable action.

An actual controller stop or driver error uses the separate recovery flow.
Teaching does not clear a recorded fault, change encoder calibration or grant
permission to resume an old target.

If reaching the all-joints-zero pose is impractical, **Reset-assisted return** is
a separate explicit option. Its application checks follow the official College
example: J2 and J3 within 10 degrees of zero, and J5 strictly between 12 and 45
degrees. A single confirmation authorizes a supported stop/reset and re-enable;
holding torque can disappear during reset. The flow rechecks the posture after
stopping, verifies disabled-motor feedback before enabling, and never sends a
position target, homing or replay. It is not an automatic fallback when the
standby/CAN sequence fails. Failure leaves the controls disarmed with the next
required step displayed. An out-of-range resting angle remains a separate range
issue after the handoff, rather than being hidden by a new zero offset.

Vendor references: [PiPER Quick Start, section 2.2](https://static.generation-robots.com/media/agilex-piper-user-manual.pdf),
[current SDK control-mode validation](https://github.com/agilexrobotics/piper_sdk/blob/b847e5393a7ce6ff8508d713eca7bdd3a42421b4/piper_sdk/piper_msgs/msg_v3/transmit/arm_motion_ctrl_2.py).
The manufacturer requires physical zero and standby before returning from
button teaching to CAN. Its alternative reset-based playback example is not
an automatic fallback for this flow. The separately confirmed reset-assisted
option is based on the [official College posture checks](https://github.com/agilexrobotics/Agilex-College/blob/c2688be41e1bc99a9addd555f237a16bc839d936/piper/recordAndPlayPos/playPos_new_en.py).

## Desk cross and known-height object

Open **Guided desk alignment check** in the selected robot's panel. This is a
manual verification record, not a solved metric calibration.

Enter the reference's scene X/Z coordinates, desk surface Y and the object's
measured height above that surface. Name the exact tool/contact point being
compared, for example `grasp_site`; a camera origin is not a fingertip. Place the
physical reference at the measured cross and its counterpart at the entered
scene position. The plugin records the reference definition; it does not infer
an object's dimensions from its name or automatically create matching geometry.

Link the correct physical and simulated robot, then start the local check. Use
the physical teach button and guide the defined tool point to the reference.
Hold still, capture the measured pose, inspect the real and simulated views,
and select whether they agree. Record an estimated gap when known. Repeat with
several different approach poses to expose joint-sign, zero-offset or tool-frame
errors that one pose can hide. Finish and save the report before changing robots.
Return to zero while hand guiding, then use the control handoff above when ready.

The report keeps robot/CAN identity, connection generation, scene mapping,
reference definition, timestamped joint observations and operator assessments.
A bounded passive trace records measured joint motion without controlling the
arm. It is separate from the robot's onboard recording and is not automatically
replayed. Reports live outside plugin packages under the selected robot's
`alignment-checks` settings directory.

Agreement between copied joint values verifies numerical linking only. Visual
confirmation at one point does not independently measure the tool position,
solve base/tool/camera transforms, establish contact force or synchronize camera
exposures. The report keeps metric calibration unverified. See the
[calibration roadmap](workspace-calibration-roadmap.md) for later depth/marker
measurements and spatial validation.

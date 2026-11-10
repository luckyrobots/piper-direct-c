# Original PiPER program: dashboard feature review

Inspected locally on 2026-10-28: ArmRobotUA V1.6.3.260828_release, on the second display. Opened navigation tabs and information windows without connecting the original program to the adapter or applying settings. Blank values and zeroes in that disconnected program are placeholders, not readings from the arm. Hardware support and exact command semantics still need protocol verification before implementing new writes.

## What the original application exposes

| Location | Observed controls | Decision for our dashboard |
|---|---|---|
| Top bar: speed | Global speed slider and numeric value, displayed as 50. Enable and E-STOP alongside it. | Keep one global motion speed control. Our existing value is degrees/second, so retain explicit units; do not silently substitute the vendor percentage. Add a compact slider paired with its numeric input later. |
| Control → Motion | MotionMode options: Move J, Move P, Move C, Move L, MIT, CPV. CtrlMode displayed CAN. J1–J6 angle entries with minus/plus, reset and edit. | Joint position control is essential and already supported. Added sliders spanning each joint's reported limits. Cartesian pose display and Cartesian jog are worthwhile next; defer circular paths and MIT/CPV until needed by Lucky Engine and independently validated. Mode names alone do not establish their exact semantics. |
| Control → Light | Joint selector, RGB channels, color preview/picker, apply. | Optional identification light to distinguish six arms. Low priority; no full color editor needed now. |
| Control → Linkage | Role instruction displayed Leader, feedback offset, command offset, address offset, apply. | Defer until leader/follower teleoperation is required. Do not expose arbitrary CAN offsets in routine controls. |
| Control → Gripper | Opening in mm, torque label, multiplier percentage, 70 mm limit selector, power assistance, enable/disable, clear error, gripper mode, BeZero; also lift-platform position/state/error controls. | Keep normal opening, enable/disable, feedback and fault information for the actual installed gripper. Standard CAN gripper is separate from Pika USB serial. Do not copy its torque/range/calibration writes to Pika. Skip lift platform without that hardware. |
| Control → DexHand | Parameter selector displayed position; thumb tip, thumb root, index, middle, ring and little finger values; details/send. | Skip: this setup uses Pika, not a dexterous hand. |
| Settings → JointSettings | Joint selector; max angle, min angle, max speed in rad/s, max acceleration in rad/s²; edit. | High priority: show the existing queried limits and acceleration limits in expandable joint details. Keep ordinary sliders constrained to those limits. Configuration writes belong in advanced setup with readback, rather than everyday motion rows. |
| Settings → EndSettings | End payload selector and apply; maximum linear speed, angular velocity, linear acceleration, angular acceleration; edit. | Useful advanced setup for the installed Pika/tool payload and future Cartesian motion. Read current settings first; confirm firmware support and units before permitting writes. |
| Settings → Collision grade | Separate J1–J6 level sliders and edit. | High priority to display current configuration and clearly decoded collision events. Editing can follow once readback and firmware support are verified. Do not silently change protection levels. |
| Settings → Assistance | Separate J1–J6 level sliders and edit. | Useful for hand-guided teaching. Add as an advanced feature after installation orientation and payload support are established; not required for current joint jogging. |
| Settings → Information → motionInformation | J1–J6 table labeled linear/angular velocity and linear/angular acceleration, with scaled units. | Show measured velocity and configured limits where the protocol actually supplies them. Preserve units and distinguish configured limits from measured values. |
| Settings → Information → statusInformation | AutoCheck, Freeze, record; per-joint status code, motor speed, angle, voltage, current, motor and driver temperature, collision, blockage, error, enabled, communication, angle state. | Highest-value addition: expandable motor diagnostics with human-readable fault explanations and a local diagnostic export. Several measurements already exist in our API/rows; expose remaining supported fields without another large permanent card. |
| Motor → motorSettings | Motor selection, enable/disable, zero-point setting, installation setting, clear error, initialization. | Keep normal robot enable/disable and guided fault recovery. Show installation orientation in advanced setup. Leave calibration/initialization out of everyday controls; moving to zero is different from rewriting zero calibration. |
| Motor → SoftwareSetting | Upgrade-log checkbox. | Skip firmware UI as requested. A concise local connection/recovery log is more useful. |

Also visible: TraceLib, ArmStatus, and 3D view help/reset/settings. TraceLib and ArmStatus were not opened in this pass. They should not be described as verified trajectory recording/playback features without further inspection. The motor and software tabs were inspected; no robot settings were changed.

## Recommended implementation order

1. **Everyday actuator controls:** compact Pika row above J1, inline camera with expanded view, joint angle sliders, live readings, one shared session prompt. Implemented in this change. Slider input previews a target; release sends it at the selected speed. Joint endpoints come from the controller. Pika uses its existing 0–90 mm linkage model, with feedback-checked steps of at most 5 mm for a long slider move; that is not a controller-reported width limit.
2. **Diagnostics drawer:** decoded controller/joint errors, collision/blockage/communication states, both temperatures where available, current/voltage, measured speed, joint limits, acceleration limits, local export. Prioritize this over cosmetic features because it makes six-arm troubleshooting practical.
3. **Tool setup:** current payload and installation orientation, read-only first; explicit advanced changes with readback once supported. Keep these separate from normal motion targets.
4. **Cartesian use:** live X/Y/Z and orientation, followed by Cartesian jogging and saved poses using a validated kinematics path. Joint angle limits alone do not describe workspace clearance.
5. **Teaching and fleet use:** hand-guiding assistance, saved named poses, optional arm identification light. Add leader/follower only when that workflow is needed.

No need now for firmware flashing, DexHand, lift-platform controls, arbitrary address offsets, advanced torque control, or a copy of every vendor dialog.

## Validation and current limits

- JavaScript syntax checks passed for app.js and pika.mjs.
- Existing shared-prompt, Pika codec/linkage, Pika lifecycle, remote relay, Windows COM-blocking and camera-profile tests passed.
- Automated browser interaction test was blocked by automatic approval review. Native visual verification of the refreshed dashboard was then stopped by Computer Use because it could not confidently determine the browser URL. New slider motion and final rendered layout have not been verified on hardware in this pass.
- The backend was not restarted and Windows was not rebooted during this change. Refresh the dashboard page to load the new static assets.
- The firewall installer reported successful persistent-task registration; its protected task cannot be queried from this unelevated session (schtasks returns Access denied). The installed refresh script contains the corrected return behavior. A later reboot remains necessary to prove unattended gripper reconnection with this fix.

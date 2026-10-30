# Calibrate a PiPER base against measured desk points

Use **Calibrate robot · desk measurements** in the native Pipers panel. This is a passive, per-robot setup step: the operator moves the real arm using its physical teach button, and the linked scene continues to display measured joints. The wizard never enables, resets, homes, replays or sends a joint target to the physical arm.

The feature fits the scene robot's **base yaw and horizontal position**. It keeps the base on the confirmed scene tabletop reference. It does not change encoder zeros, joint signs, tool offsets, model scale, base tilt or camera calibration. A successful desk check is not a claim of pixel alignment, synchronized exposure or perfectly accurate datasets.

## Desk frame and measurements

Stand at the **front long edge** of the real desk, facing across it. **LEFT** is the short edge on your left; **BACK** is the far long edge, furthest from you. Use these same edges for every measurement.

After confirming the simulated tabletop and entering its measured width/depth, the wizard shows a temporary pointer on the simulated **LEFT** edge and asks whether it is the edge you will measure from. The next question points to the **BACK** edge. Each confirmation requires an acknowledged pointer on the current robot/model/question; it cannot proceed with an unavailable, stale or wrong pointer. These are visual scene references, not physical motion targets. The operator must compare the simulated edges with the real desk rather than approving a set of axes.

Advanced implementation detail: the current scene desk is centred on world X=0/Z=0 in Lucky Engine's Y-up world, with left at negative X and back at negative Z. A desk elsewhere or rotated in the scene must be corrected first; the wizard does not infer a tabletop frame from a camera. This detail is available in a tooltip rather than as the primary setup instruction.

The wizard asks **one question at a time**. It first asks **Is the simulated robot base resting on the tabletop?** Choose **Use this tabletop reference** only after checking that scene relationship. This reads the linked model's validated metric root reference; it neither moves scene geometry nor claims a physical height measurement. A mismatched simulated root/tabletop must be corrected separately. The technical world-Y value is recorded for reproducibility, rather than requiring the operator to type it during the normal flow.

Each step shows one concrete instruction, with units where an answer is required. The visible progress is **4 reference points + 1 check point**, rather than solver terminology or internal step names. Hover **(i)** for detail; clicking it does not advance or edit the step. The hand-guiding and Capture steps keep support/holding and 0.4-second stationary requirements visible. The help column labels LEFT/BACK/FRONT/RIGHT in a top-down desk diagram beside the existing camera preview; JPEG/metric limitations live in its information tooltip.

**Physical floor origin and floor-to-table height are not required for tabletop calibration.** The required physical measurements are desk width/depth and each contact point's left-edge distance, back-edge distance and height above the tabletop. Its mapped world Y is simply `scene tabletop Y + measured height above tabletop`. An arbitrary confirmed scene tabletop reference is valid; it need not equal the real desk's height above the floor.

Unknown absolute office heights remain unset. Older explicitly entered floor/table-height answers are preserved as nullable **optional visual-office audit** evidence only; they never block capture, fitting or Apply and are never silently marked measured. Existing drafts resume on the tabletop-relative questions instead of forcing legacy floor questions. A separate office-height note, such as a measured desk height, does not become a robot-calibration constraint.

```text
                   BACK edge (world Z = -depth/2)
             +----------------------------------+
 LEFT edge   |       back distance              | RIGHT edge
 (X=-W/2)    |              |                   |
             |<-- left ---- P                   |
             |                                  |
             +----------------------------------+
                        FRONT

 Pworld = (-width/2 + left_mm/1000,
           surface_y + height_above_desk_mm/1000,
          -depth/2 + back_mm/1000)
```

No object is required. A point drawn on the desk has height zero. Measure where the **defined contact point** actually is, not a convenient different part of the gripper. Enter the precision you can reasonably achieve with the ruler/mat, from 1 to 10 mm. The default 10 mm permits training RMS up to 10 mm and maximum errors up to 20 mm; these are acceptance bounds, not a promise of 10 mm physical accuracy.

A grid mat is optional. Secure it flat and align its rows/columns with the desk edges; a rotated grid frame is not supported. In grid mode, enter its measured square size and the world-desk edge distances to intersection (column 0, row 0). Columns increase from left to right and rows from back to front. Use intersections, not square centres. Verify the actual square size with a ruler, including print scale for a printed mat; a nominal size alone is not a measurement. Include the mat's thickness in the contact point's **Above desk mm** height. The default 50 mm square size is editable; a 10 mm grid is also supported.

## Fixed tool/contact point

The engine must expose a named metric model site. The suggested `grasp_site` may intentionally represent a grasp centre behind the jaw tips; it is not universally the physical tip midpoint. Verify that your chosen site is exactly the physical midpoint between the jaw tips used for measurements. Both tips resting at the same height can define that midpoint; one tilted fingertip touching the desk usually does not. If the model's site represents another point, use/fix the correct named site instead. The camera origin, a jaw corner and an unspecified fingertip are not interchangeable.

First choose the physical midpoint, then inspect the selected model point using **Show selected point**. It reads the site coordinates and requests an ephemeral viewport marker without opening a camera or moving geometry. If the current model exposes exactly one `calibration_jaw_tips`, **Show midpoint between jaw tips** explicitly selects that discovered reference. The operator must then choose **Marker matches jaw tips** after comparison; software never silently approves physical correspondence. Technical site identity and precise coordinates are available in the information hint. A CAD tip-face midpoint is still not proof of the real hardware contact point.

Compare the marked model point with the physical midpoint and explicitly report correspondence. This remains an **operator statement**, not a software-verified TCP calibration. The chosen site stays fixed for all captured points. The marker follows existing FK, expires after 30 seconds, and refreshes at most once per 10 seconds only while the same fresh robot/subscription/model/site remains in calibration. Leaving calibration clears the exact pinned marker; it is retained when opening an enlarged camera from the wizard. Marker failures affect wizard feedback only, never motion permission or hardware. The native host also clears markers on expiry, model/root/mode changes, unsubscribe or plugin unload.

The engine reports the compiled model asset ID, compiled source/dependency revision and world FK; the wizard rejects a changed asset, root placement, robot, scene, backend session or CAN connection rather than combining incompatible points. Model geometry changes, including adding a new calibration site, require a new compiled revision and recaptured measurements.

## Capture and validate

1. **Prepare:** confirm the selected robot/CAN/model and link it in Controls. Finish movement before measuring. Leaving controls removes scene-to-real target permission; workspace navigation handles any existing gesture separately. Shared bench preview and background cameras/telemetry continue.
2. **Identify the point:** choose the physical midpoint between both jaw tips, inspect the marked model point, and explicitly report correspondence.
3. **Measure setup:** confirm the simulated tabletop reference, then answer width and depth individually. Compare the LEFT pointer, confirm it, then compare and confirm the BACK pointer. Enter your measurement precision. There are no mandatory physical floor/table-height questions. Select edge measurements or a secured, aligned grid mat; grid setup asks square size and origin offsets separately.
4. **One point:** choose a location, answer its left/back/height questions (or grid column/row/height), then single-click the arm's physical teach button for solid green hand guiding. Guide the point by hand and hold still at least 0.4 seconds. Only the explicit **Capture** button records it. Height zero must be entered deliberately, including mat thickness where applicable.
5. Capture at least **four distinct reference points**, spread in both desk directions with at least a 10 cm overall span. Points on one line or tightly clustered points are rejected. Every new point starts with blank location/height answers; values from the previous point are not invented as another observation.
6. Capture at least **one new check point**, at least 5 cm from every reference point. A saved profile's measured cross may be reserved for this purpose; it is not a universal factory coordinate. Choosing that shortcut explicitly uses the saved edge measurements and still asks the operator to verify them and enter contact height. The check point does not participate in fitting; failure blocks Apply. Reports retain the technical training/validation distinction.
7. **Review:** choose **Calculate alignment**. Inspect the proposed base-centre edge distances/direction and point errors. The tooltip/report retains full position, per-point residuals, training RMS/maximum, validation maximum and height RMS. Outer mounting clearances must still be checked against the actual nearest base edges.
8. Choose **Apply to simulation** explicitly. The engine verifies expected original root, compiled model ID and revision atomically, updates scene placement/FK, and emits no joint target. Measured joints remain linked. **Undo alignment** restores the original scene position/yaw using the verified post-apply root as its precondition.

**Previous question** edits only the current uncaptured point/setup path. Captured observations stay in the report. **Start over / edit setup** preserves the previous report and asks before archiving pending/applied rollback context. Per-robot question progress and draft text are saved separately, so Back to Controls and return can resume the questions. Every step has an event epoch in addition to robot/page identity: delayed text, Continue or Capture from an older step cannot alter a newer point. Resume never enables, moves, recaptures or auto-applies the robot.

The camera-help column reuses the selected already-running Bench/Pika/depth preview. These are JPEG previews, not queried metric depth measurements. The camera image and displayed site coordinates help the operator compare views; they do not certify correspondence or data accuracy.

If a placement response is interrupted, the wizard blocks another write and offers **Check scene placement**. That action only reads the root. It distinguishes the original placement from the proposed placement and will not overwrite a third, unrelated scene edit. No automatic Apply, rollback or retry occurs.

After a plugin restart, a saved pending/applied placement restores its original/proposed/applied rollback context and is visibly **unverified** until that read-only check. A changed CAN/backend identity keeps old fit writes blocked. **New / edit settings** then offers an explicit archive confirmation if placement/rollback context remains; it keeps the complete previous report on disk and never silently undoes or repeats the old write.

For a different desk/TCP or a new connection, use **New / edit settings**. Existing reports remain on disk. Returning from hand guiding is a separate supported teaching flow in the normal robot controls; finishing calibration never homes or replays the real arm.

While the wizard is open, target/enable routes are blocked in the command handler as well as hidden widgets, including queued viewport events and hotkeys. Global STOP and Disable remain available. Immediately before a scene write, after saving its pending report, the plugin rechecks the physical session, fresh healthy status, stationary pose and teaching state. An interrupted or changed context sends no scene write.

## Rejected fits

A persistent vertical mismatch suggests a wrong contact point, wrong desk height, wrong model or joint-zero mapping. The solver does not raise or tilt the base to hide it. An inconsistent training fit or a failed independent validation similarly indicates measurement, TCP or model/joint problems; a base translation cannot repair arbitrary joint geometry.

All captures require healthy fresh six-joint feedback, a stationary hand-guided arm, matching scene joints within 0.01°, and a scene read completed within 500 ms. The six measured angles, model FK point, world root, point measurements, model/connection identity and pose fingerprint are preserved for inspection. These timestamps are host receipt times, not synchronized camera exposures.

## Saved data

Settings and reports live outside the plugin package under the engine's per-plugin settings directory:

```text
calibration/<robot-profile-id>/settings.json
calibration/<robot-profile-id>/<timestamp>-<id>-calibration.json
calibration/<robot-profile-id>/wizard-draft.json
calibration/<robot-profile-id>/<timestamp>-<id>-wizard-evidence.json
```

Reports include the desk frame, operator precision, fixed tool site, actual compiled model asset/revision, CAN generation/backend-session fingerprint, training and validation measurements, measured joints, FK point fingerprints, residuals, original/proposed/applied placement and placement-verification status. The server authentication token is not stored. The existing `alignment-checks/` visual observation reports are preserved separately.

## Engine capability contract

`robot.read` provides `metricPlacementSupported`, `metricFrame: "engine-world-y-up"`, `modelAssetId`, `modelRevision`, `rootPlacement` and named `toolSites` in metres. Unsupported parenting, tilt or scale is reported rather than guessed. Scene-only `robot.placement` accepts IDs, `expectedModelAssetId`, `expectedModelRevision`, `expectedRootPlacement`, target world position and absolute world-Y yaw in `[-π, π]`. It supports native scene undo without generating a physical robot target.

Read-only `robot.highlightToolSite` uses the same scene/root/model revision identity, exact site name and bounded duration. It requires that plugin process's matching robot subscription. `visible:false` clears only the caller's marker. It is a visual overlay, not an entity, target, calibration result or hardware command.

Read-only `robot.highlightReference` uses the same ownership and expiry rules, with an explicit world-space point/label instead of a named site. The wizard derives LEFT/BACK midpoints only from its explicitly confirmed simulated tabletop reference and measured width/depth. The host must echo the exact requested point and label before a confirmation is available; it is requested again when the operator confirms, so a replaced model or subscription cannot reuse an earlier acknowledgement. Changing step/profile/connection/model/dimensions invalidates that acknowledgement. New schema-2 drafts append the BACK step without renumbering old steps; uncaptured schema-1 drafts repeat the new pointer questions instead of inventing confirmations. Existing saved reports and pending scene-write inspection remain preserved.

An older editor that does not support desk pointers shows **Update Lucky Engine to show desk markers, then retry Show edge**. Failed requests keep confirmation unavailable and wait for the explicit Show edge action rather than retrying silently. Before every new capture, both edge confirmations must match the current robot/model identity and the exact desk measurements. Older reports remain inspectable, but adding points requires the new edge confirmations; start new questions when necessary.

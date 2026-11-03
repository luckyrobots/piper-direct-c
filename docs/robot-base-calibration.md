# Calibrate a PiPER base against measured desk points

Open **Calibration** in the native Pipers workspace. The page shows all four reference points (**P1–P4**) and the independent **Check** point together. There is no Next/Back wizard.

Calibration is passive: move the real arm by hand using its physical teach button while the linked simulation displays its measured joints. Selecting a mark only frames the viewport. Calibration never enables, resets, homes, replays or sends a movement target to the real arm. **Apply to simulation** changes the simulated base placement only.

The fit adjusts **base yaw and horizontal position**, keeping the base on the confirmed scene tabletop. It does not change encoder zeros, joint signs, model scale, base tilt, tool offsets or camera calibration. A passing desk check does not establish camera pixel alignment or synchronized recording.

## Prepare the sheet

1. In **Controls**, choose the correct physical robot and scene model, finish any movement, and click **Link robot**. Open **Calibration**.
2. Enter the full desk **width** and **depth** in millimetres. Enter **Measurement accuracy ± mm** from **1 to 10**, or explicitly choose **Use ±5 mm**. This estimates ruler/contact uncertainty; it is not a claim of achieved accuracy. Zero is invalid.
3. Click **Show LEFT** and compare its pointer with the physical edge you will measure from. Click **Confirm LEFT**. Repeat **Show BACK → Confirm BACK**. The buttons show when each confirmation is complete.
4. Click **Show jaw tips**. Compare its marker with the model point that corresponds to the midpoint between both physical jaw tips, then click **Confirm jaw tips**. This confirmation becomes available only after the current marker is acknowledged. Re-showing the same unchanged point preserves its confirmation.
5. Check that the simulated robot base rests on the simulated tabletop and click **Base is on desk**. A mismatched scene desk/root must be corrected separately.

Dependent controls unlock as these checks are completed. The page shows the next required action and each point's current status; a disabled Capture is accompanied by a visible reason. Setup confirmations are tied to the robot connection, model revision, dimensions and base placement. A stale confirmation is shown as pending rather than accepted by name alone.

## Enter and capture the five marks

Draw four reachable crosses on the bare tabletop. Spread them across both desk directions, rather than along one line; the set needs at least 100 mm of overall spread in both directions. Closely spaced reference marks are allowed; only duplicate locations within **1 mm** are blocked. The full reference set must still have enough spread to constrain the alignment. Reserve a fifth cross at least **50 mm from every reference mark** for the independent Check. An existing measured centre cross is suitable for that final check.

Enter all five pairs in the sheet's **BACK mm / LEFT mm** columns. For example, a physical annotation of `20 / 69.7 cm` means **BACK 200 / LEFT 697 mm**. All contacts in this sheet are on the bare desk, so their height is **0 mm**; do not hover above the mark. Duplicate or overlapping entries show an inline row error before capture.

For each reference row, in order:

1. Click **Select P1** (then P2, P3 and P4). The row becomes selected and its numbered cross is framed in the viewport. This does not move hardware.
2. Single-click the robot's physical teach button for **solid-green hand guiding**.
3. Move the arm **by hand** until the midpoint between both jaw tips gently touches that physical cross. Do not push into the desk. Both jaw tips must define the same contact midpoint; one tilted jaw corner is a different point.
4. Hold still for at least **0.4 seconds**, then click **Capture P1** beside the selected row. Capture records the current measured pose. It does not move or power the arm.
5. Check that the row says **Captured** and the page count advances. Repeat for P2–P4, then select and capture **Check** at the fifth cross.

The header shows **0/4 captured + 0/1 check**, updating as observations are saved. Captured coordinates become fixed. Later rows cannot be captured ahead of the next required row, but all measurements remain visible and uncaptured entries remain editable.

Measurements save automatically **as you type**, including before Enter or losing focus. The save status acknowledges the local atomic write. Desk settings and all ten point coordinates restore in a new plugin session; captured observations are retained in the report. Restoring a draft never moves, enables, recaptures or applies anything automatically.

## Viewport and camera help

Entered measurements have numbered P1–P4 and Check counterparts on the scene desk. **Select Pn** frames one mark; **Show all crosses** frames the set. The crosses lie in the tabletop plane and change perspective with the view. Background renewal keeps them visible without repeatedly moving the viewport.

The jaw-tip marker follows the model's measured forward kinematics. It is separate from the desk-edge and numbered mark previews. Markers expire or clear when their model, robot, scene, subscription or mode becomes invalid, or when the plugin unloads. A failed preview reports its reason and never authorizes a capture.

The camera-help column uses the selected Bench, Pika or D405 preview. The shared bench camera also remains in the robot sidebar. Camera previews help compare contact and framing; they are not metric depth measurements or proof of physical correspondence.

## Fixed desk frame and contact point

LEFT and BACK are **fixed edges of the calibrated desk**, not labels that change with where the operator sits. Confirm the scene pointers against the same physical edges and use those edges for every measurement.

The supported scene desk is centred at world X=0/Z=0 in Lucky Engine's Y-up frame: LEFT is X=-width/2 and BACK is Z=-depth/2. A translated or rotated desk frame must be corrected separately; the plugin does not infer it from a camera.

```text
                   BACK edge
             +-----------------------------+
 LEFT edge   |             | back          | RIGHT edge
             |<-- left ----P               |
             |                             |
             +-----------------------------+
                   opposite long edge

 Point world = (-width/2 + left_mm/1000,
                 confirmed_scene_tabletop_y,
                -depth/2 + back_mm/1000)
```

Physical floor height and floor-to-table height are **not required** for this tabletop-contact fit. Older explicit office-height notes are retained as optional evidence; they do not become robot-calibration constraints.

The engine must expose an exact named metric model site for the chosen physical contact point. A model's `grasp_site` may be a grasp centre behind the jaw tips. If the model exposes one `calibration_jaw_tips`, the sheet selects that discovered reference when showing the jaw tips. The camera origin, an unspecified fingertip, and a tilted jaw corner are not interchangeable with the midpoint.

**Confirm jaw tips** is the operator's visual statement, not an independent software verification of the physical TCP. The site stays fixed for all captured points. A changed model geometry/revision or changed contact point requires new observations.

The current sheet is for bare-tabletop contacts only. Old reports with raised points or grid/mat thickness are retained and are not silently converted into height-zero contacts. Start **New calibration** for the current workflow; a mat with nonzero thickness needs an appropriate height-aware workflow rather than pretending it is the bare desk.

## Calculate, apply and recover saved work

After all five captures, click **Calculate alignment**. Inspect the proposed base-centre distances, yaw, reference-point mismatch, independent Check error and height mismatch. The Check point is excluded from the fit and must pass independently. Reports preserve per-point errors and the stated measurement accuracy.

Click **Apply to simulation** only after the result passes. The engine atomically verifies the expected root placement and compiled model identity before changing the simulated base and its forward kinematics. Measured joints remain linked; no physical joint target is generated. **Undo alignment** restores the verified original scene position/yaw.

An interrupted placement response or a restored applied placement requires **Check saved placement**, a read-only inspection. It distinguishes the original placement from the proposed placement and will not overwrite an unrelated scene edit. There is no automatic Apply, Undo or retry.

For a new desk, contact point, model or incompatible connection, choose **New calibration**. Existing reports remain on disk; pending/applied rollback context requires explicit archiving. Returning from physical hand guiding is a separate supported teaching flow in **Controls**. Finishing calibration does not home or replay the arm.

### Re-evaluate a corrected model without repeating physical captures

When the same model asset receives a source correction, the panel offers **Re-evaluate saved poses**. Keep the same robot connection, original base placement, desk and named physical contact point. An applied or unverified alignment must be undone and checked first. A different robot, model asset, connection or contact point requires a new calibration.

Reopening the editor can assign the loaded scene a new session UUID. The same explicit action can rebind saved observations to that new scene session, including when the model revision itself is unchanged. This is allowed only for the same robot entity UUID, profile, CAN device/generation, backend session, model asset, original base placement and named contact point. The old and new scene UUIDs are recorded in the report and audit. Ordinary Capture and Apply retain their strict scene-identity checks; they do not silently resume an old session.

This explicit action sends each saved set of six arm angles to the engine's read-only `robot.evaluate` API. The engine evaluates the named contact site in private kinematic data; the live simulated pose and real arm remain unchanged. The API must confirm that this site depends on exactly those six saved arm joints. A site that also depends on an unrecorded finger joint cannot be recomputed by borrowing its current position.

The complete original report, operator confirmations and each engine evaluation receipt are retained under `model-reevaluations/`. Only the derived scene points and model revision are replaced. Ruler measurements, physical angles, acquisition times, original pose fingerprints and the independent Check role remain intact. The report's `modelReevaluation` field identifies this derivation; it does not describe a fresh physical capture.

The old alignment fit is discarded. Reconfirm LEFT, BACK, the jaw-tip marker and that the unchanged base rests on the desk, then choose **Calculate alignment**. Apply remains subject to the same reference and independent-check errors. Re-evaluation does not fit encoder offsets or change thresholds to make a result pass.

The reported base location refers to the **rotation axis of the bottom joint (J1)**, projected onto the desk. This is not necessarily the geometric centre of an asymmetric mounting plate; measure mounting-edge clearances separately.

## Capture and fit checks

Captures require healthy, fresh six-joint/controller feedback, physical hand guiding, a stationary pose, matching simulated joints within 0.01°, and an engine pose read completed within 500 ms. Live motion, stale connection/model/root evidence and invalid measurements are rejected. The UI reports the blocking condition rather than leaving the operator to infer it from a disabled button.

The solver rejects duplicate, clustered or nearly collinear reference sets and a failed independent Check. A persistent vertical mismatch can indicate a wrong contact point, wrong scene tabletop reference, model error or joint-zero mapping. The solver does not raise or tilt the base to hide that mismatch.

The saved evidence includes the six measured angles, model FK point, root placement, coordinates, compiled model revision, connection identity, pose fingerprint and residuals. Timestamps are host receipt times, not synchronized camera exposure times.

## Check jaw height at new poses

After applying an alignment, use **Check jaw height** at the top of Calibration.
This is an independent check, not another four-point calibration:

1. Support the arm and single-click its physical teach button for the solid-green
   hand-guiding light. Move the closed jaws to a comfortable position above the desk.
2. Watch the live **jaw height** and the vertical viewport line from the jaw-tip
   midpoint to the tabletop. The line follows the measured robot, not a commanded
   destination. Its lower endpoint is directly beneath the jaw, not at the desk cross.
3. Hold the arm still and choose **Measure this pose**. The displayed prediction is
   pinned to that pose while you use a tape measure.
4. Measure vertically from the tabletop to the midpoint where the closed jaw tips
   meet. Enter millimetres and choose **Save check**. Positive error means your tape
   measurement is higher than the model predicts; negative means it is lower.
5. Choose **Check another pose** and repeat at a different height or extension.

Measure the same physical point every time. Do not measure diagonally along the
arm or to the wrist/camera. If the projection is beyond the desk edge, move the
jaw back above the tabletop before measuring. A prediction below the desk plane
is shown as negative, not silently changed to zero.

Live preview works while moving. Recording a comparison requires fresh, healthy,
stationary measured feedback and the matching scene pose. If you move after pinning
a pose, return to Live and pin the new pose instead of assigning the old ruler
measurement to it. Stale feedback or a different model/root invalidates the live
reading; missing feedback is never displayed as zero height.

Height checks and in-progress ruler text save per robot in `height-checks.json`.
They preserve the predicted and measured heights, signed error, physical joint
angles and model/root identities. Reopening the plugin restores the evidence,
not permission to resume live checking or trust a previous pose automatically.
These checks never change the saved alignment, joint zeros or tool offset, and
send no real robot motion, teach, enable or reset command.

Base alignment changes yaw and horizontal position only. Height checks therefore
test the vertical model/tool reference at new poses; they do not by themselves
verify horizontal placement, camera calibration or recording timestamps.

## Saved data

Settings and reports live outside the plugin package under the engine's per-plugin settings directory:

```text
calibration/<robot-profile-id>/settings.json
calibration/<robot-profile-id>/<timestamp>-<id>-calibration.json
calibration/<robot-profile-id>/wizard-draft.json
calibration/<robot-profile-id>/height-checks.json
calibration/<robot-profile-id>/<timestamp>-<id>-wizard-evidence.json
```

The legacy `wizard-*` filenames remain for compatibility; the current interface is one sheet. Authentication tokens are not stored. Existing `alignment-checks/` visual reports remain separately available in Diagnostics.

## Engine capability contract

`robot.read` provides `metricPlacementSupported`, `metricFrame: "engine-world-y-up"`, `modelAssetId`, `modelRevision`, `rootPlacement` and named `toolSites` in metres. Unsupported parenting, tilt and scale are reported rather than guessed.

Scene-only `robot.placement` accepts scene/robot IDs, expected model asset/revision and root placement, a world position and absolute world-Y yaw. It supports native undo without generating a physical robot target.

Read-only `robot.highlightToolSite` and `robot.highlightReference` require the plugin's matching robot subscription and pinned scene/root/model identity. Reference requests include an exact world point/label and expected root placement; the host must echo the requested point, label, framing and root before acknowledgement is accepted. Numbered crosses use separate bounded marker IDs. Manual selection frames the viewport; maintenance does not. Model, connection, dimensions or root changes invalidate the relevant proof.

An older editor or hidden viewport/overlay reports an actionable preview error. Confirmation remains unavailable until a valid pointer is acknowledged. Preview events preserve the current sheet's IDs, so a visual action cannot invalidate the next button that the operator is about to click.

### Correct a mistyped measurement

For a captured row, choose **Edit entry**, correct BACK/LEFT, then **Save entry**. This changes only the ruler coordinates assigned to the already captured pose; it does not move or recapture the arm. The original report is retained under measurement-corrections, the old fit is invalidated, and Calculate alignment must be run again. Cancel edit restores the captured coordinates. Applied or unverified alignments must be undone/verified before correcting entries.


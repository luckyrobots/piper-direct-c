# Calibrate a PiPER base against measured desk points

Use **Calibrate robot · desk measurements** in the native Pipers panel. This is a passive, per-robot setup step: the operator moves the real arm using its physical teach button, and the linked scene continues to display measured joints. The wizard never enables, resets, homes, replays or sends a joint target to the physical arm.

The feature fits the scene robot's **base yaw and horizontal position**. It keeps the base on the measured desk surface. It does not change encoder zeros, joint signs, tool offsets, model scale, base tilt or camera calibration. A successful desk check is not a claim of pixel alignment, synchronized exposure or perfectly accurate datasets.

## Desk frame and measurements

The current desk frame is Lucky Engine world Y-up. The desk is centred on world X=0/Z=0, left is negative X, and its back edge is negative Z. Set up that scene desk frame before measuring. Width and depth are the complete tabletop dimensions; surface Y is the tabletop's world height. The defaults are 1400 × 700 mm with surface Y=750 mm. A desk elsewhere or rotated in the scene must first be brought to this frame; the wizard does not infer a tabletop frame from a camera.

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

The engine must expose a named metric model site, normally `grasp_site`. Verify in the model that this site is exactly the physical midpoint between the jaw tips used for your measurements. Both tips resting at the same height can define that midpoint; one tilted fingertip touching the desk usually does not. If the model's site represents another point, use/fix the correct named site instead. The camera origin, a jaw corner and an unspecified fingertip are not interchangeable.

Use **Confirm desk frame + TCP** to confirm both the stated scene desk frame and that point correspondence once per new session. The site stays fixed for all captured points. The engine reports the compiled model asset ID, compiled source/dependency revision and world FK; the wizard rejects a changed asset, root placement, robot, scene, backend session or CAN connection rather than combining incompatible points.

## Capture and validate

1. Link the scene robot. Finish any movement before opening the calibration view. Opening the view removes scene-to-real target permission and disables viewport teleoperation in the scene; it sends no physical stop or power command. Shared bench preview and background cameras/telemetry continue.
2. Single-click the arm's physical teach button. Solid green is hand guiding. Guide the named contact point to a measured desk point, enter its left/back/height measurements, hold still for at least 0.4 seconds, and start the calibration.
3. Capture at least **four distinct training points**. Spread them in both tabletop directions, with at least a 10 cm overall span; points on one line or tightly clustered points are rejected. Record each point's own measured edge distances. Repeated readings at one location do not provide another independent geometric constraint.
4. Capture at least **one new validation point**, at least 5 cm from every training point. Validation points do not participate in fitting. A failed validation point blocks Apply.
5. Choose **Fit / preview**. Review the proposed base position/yaw, changed base-centre edge distances, per-point residuals, training RMS/maximum, validation maximum and height RMS. Outer mounting clearances are separate: the wizard does not know the physical base footprint, so recheck the actual nearest base edges against your mounting measurements.
6. Choose **Apply to SCENE only** explicitly. The engine verifies the expected original root and compiled model ID atomically, updates scene placement/FK, and emits no joint target. Measured joints remain linked. **Roll back scene placement** restores the original scene position/yaw, using the verified post-apply placement as its precondition.

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
```

Reports include the desk frame, operator precision, fixed tool site, actual compiled model asset/revision, CAN generation/backend-session fingerprint, training and validation measurements, measured joints, FK point fingerprints, residuals, original/proposed/applied placement and placement-verification status. The server authentication token is not stored. The existing `alignment-checks/` visual observation reports are preserved separately.

## Engine capability contract

`robot.read` provides `metricPlacementSupported`, `metricFrame: "engine-world-y-up"`, `modelAssetId`, `modelRevision`, `rootPlacement` and named `toolSites` in metres. Unsupported parenting, tilt or scale is reported rather than guessed. Scene-only `robot.placement` accepts IDs, `expectedModelAssetId`, `expectedModelRevision`, `expectedRootPlacement`, target world position and absolute world-Y yaw in `[-π, π]`. It supports native scene undo without generating a physical robot target.

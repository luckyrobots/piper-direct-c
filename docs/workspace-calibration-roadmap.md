# Workspace scanning and calibration roadmap

Metric scanning and fitting below are planned functionality, not a claim that the
current cameras or scene are calibrated. The plugin includes factory D405
intrinsics, a bundled fisheye lens-calibration workflow and a
[manual desk-alignment observation check](teaching-alignment.md). Camera mounts,
desk registration, inter-robot transforms and exposure synchronization still
require independent validation.

## Sources

- **Attached D405:** guided close-range observations of the desk, markers, nearby
  bases and tool. Its specified operating range is 7–50 cm. A single wrist view
  cannot measure the entire room or every obscured robot.
- **Optional phone/tablet scan:** import metric depth, camera poses, timestamps
  and a reconstructed mesh from a LiDAR-capable iPhone or iPad. Detect support
  before offering capture; LiDAR is not present on every iPhone. Start with file
  import, then consider a companion app with a local pairing code. Phone hardware
  and an Apple account must not be installation requirements for Pipers.
- **Manual measurements:** retain ruler-entered desk dimensions and nearest-base
  edge distances as constraints and as a fallback when scanning is unavailable.

Manufacturer references: [D405 specifications](https://www.intel.com/content/www/us/en/products/sku/229218/intel-realsense-depth-camera-d405/specifications.html),
[Apple scene-reconstruction capability check](https://developer.apple.com/documentation/ARKit/ARWorldTrackingConfiguration/supportsSceneReconstruction%28_%3A%29).

## Guided workflow

1. Select the robot profile and camera by stable hardware identity. Show which
   observations and transforms are already calibrated, estimated or missing.
2. Print a dimensioned marker board, check its scale with a ruler, and place its
   origin at the measured desk cross. Define the desk axes visibly in both views.
3. Capture a stationary reference frame. Detect the desk plane and board; show
   coverage and residuals, rather than silently accepting a weak estimate.
4. Guide several viewpoints: “Move J1 left until the highlighted board is visible,”
   then right and to a different tilt. Use a highlighted joint and a target band,
   not an ambiguous direction alone. Collect measured joint positions with each
   depth/RGB observation. A hand-repositioned arm needs appropriate support when
   its motors are off. Moving to a viewpoint must be an explicit operator action.
5. Estimate camera-to-tool and base-to-desk transforms using several distinct
   poses. Joint signs, offsets and tool geometry must be checked independently;
   fitting one transform must not hide a wrong joint mapping.
6. Fit desk edges and base footprints, merge registered depth observations, and
   use a phone scan for wider room geometry where available. Mark unseen surfaces
   as unknown; show uncertainty on estimated distances.
7. Overlay the result on the real image and the engine scene. Ask the operator to
   confirm identified corners, the cross and the correct robot-to-CAN assignment.
   Validate against held-out views and ruler distances before accepting it.
8. Save an inspectable calibration report outside the plugin package. Keep the
   last accepted result until the new result passes validation and is accepted.

An optional later verification can compare a known tool point with a marked
object on the desk. It must not automatically drive the arm into the desk or
infer contact merely from a camera image. Visual confirmation is a useful check,
not proof of force/contact or collision safety.

## Stored result

Persist units and coordinate conventions, board dimensions, device serials,
stream sizes, lens profiles, base/tool/camera transforms, measurement and timing
uncertainty, coverage, validation residuals, raw observation references and the
calibration version. Each robot has its own base/tool/wrist transforms; the bench
camera and desk frame are shared. A changed mount, stream mode or device identity
invalidates the affected calibration without discarding unrelated results.

## Delivery order

1. Current work: persistent multi-robot profiles, explicit CAN assignment, shared
   bench camera and a measured four-robot scene.
2. Guided board registration and attached-depth measurement, implemented with
   the bundled C# camera runtime and the engine's existing depth functionality.
3. Optional LiDAR scan import, registered to the same board/desk coordinates.
4. Synchronized recording and a separate predictive collision/planning layer
   validated against calibrated geometry and uncertainty.

A rendered model or imported room mesh alone does not prevent hardware collision.
The existing position-feedback obstruction stop remains a separate reactive check.

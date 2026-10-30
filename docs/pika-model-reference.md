# Pika model reference and calibration contact

The Pipers plugin displays measured Pika opening using the canonical paired
slide joints `joint7` and `joint8`, with equal, opposite ranges in metres.
In the audited PiperPod/Pika model, `q=0` is **open**; the signed endpoints
are **closed**. Feedback therefore maps a measured opening `w` in millimetres
to closing travel `(maximumOpeningMm - w) / 2000` per finger. This convention
must be checked for any replacement model. Joint names and limits alone do
not establish a different mesh's physical contact geometry.

The plugin does not change the real gripper's encoder calibration, firmware,
joint zeros or mechanical limits when it displays these measurements.

Fresh negative encoder readings within 0.25 mm of the closed stop are displayed
as zero opening and map to the model's closed endpoints. Larger negative values
are rejected rather than treated as calibrated measurements. This bounded
feedback allowance never changes outgoing motor targets. A disconnected or
stale gripper keeps its last scene pose and shows an unavailable reading in the
controls; cached telemetry is not presented as a live opening.

## Audited CAD reference

These numeric references came from the actual visual finger vertices in the
AgileX `pika_ros` source snapshot
`1ec77880011af608f7f6af8d3c0e7c163b300984`, not from approximate collision boxes.
No STL files or engine content pack are distributed in this repository.

| Reference | Value |
| --- | --- |
| Left STL SHA256 | `16de0af984683c4286bf826557c1ea3c40380c64f890969cde5c880726c7474b` |
| Right STL SHA256 | `0a10906fa2ed940db0de90b0f171db3cc550c8cfd56b83bebd33b9a8955e0b66` |
| Inner-jaw gap at `joint7=joint8=0` | `94.04438247823715 mm` |
| Closed `joint7` endpoint | `-0.0470221912391186 m` |
| Closed `joint8` endpoint | `+0.0470221912391186 m` |
| Suggested named contact site | `calibration_jaw_tips` |
| Site position in `pika_base` | `(0.190341918409, -0.0000095, 0.000632584249) m` |

The proposed site is the midpoint of the centers of both outermost jaw-tip
faces. It stays centered as equal opposing fingers open and close. It is a
**CAD reference**, not proof that the physical robot touches at that point.
The original `grasp_site` at `(0.135, 0, 0.0064) m` is about 55 mm behind
those faces. It is a different tool reference; a base calibration must not
hide that difference.

For that exact audited geometry, the paired joint ranges and the gripper
actuator control range should end at the closed values above. The original
`+/-0.05 m` endpoints put the visual inner faces approximately 5.96 mm past
each other. A model with different meshes or mounting must be measured again.

## Use the guided calibration flow

1. Compare the physical contact point you can measure with the exact named
   site returned by the current model. The wizard can show that site's
   coordinates and a temporary viewport crosshair.
2. If you use both jaw tips, compare their physical midpoint with the CAD
   tip-face midpoint. Confirm correspondence explicitly. Do not confirm an
   arbitrary `grasp_site` because its name sounds appropriate.
3. Measure several distinct desk locations, then keep a separate location
   for independent validation. Camera previews assist placement; a colorized
   depth JPEG is not a metric depth measurement.
4. Apply a scene-base correction only after the fit and independent check
   meet the reported tolerance. Physical joint-zero errors, wrong link
   geometry or an incorrect tool point require separate correction.

Changing model geometry or a named site changes its source revision. Start a
new calibration session after a model change; existing samples cannot be
reused against a different tool model. Keep backups of model files before
making local edits.

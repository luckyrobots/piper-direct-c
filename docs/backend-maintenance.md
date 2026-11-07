# Backend restart

`POST /api/maintenance/restart` is an explicit maintenance action, separate from arm recovery. It uses the current `X-Piper-Token` for HTTP or `x-piper-token` for gRPC Execute, and the normal session-owner guard. Read `/api/state` immediately before sending:

```json
{
  "owner": "current-plugin-owner-guid",
  "backend_process_id": 123,
  "backend_instance_id": "instance-from-current-state",
  "release_grippers": true
}
```

The confirmation should say **“Grippers empty—restart”**. Restart closes the Pika agents, which release power they requested. It does not reset, disable, home, or send a position to an arm. An arm’s persisted STOP/recovery remains latched after restart.

Stop simulation following through the plugin’s normal measured-hold flow first. The endpoint does not interrupt active motion, stop another owner’s movement, or acquire another owner’s powered gripper. It rejects an active arm action, metadata query, teaching/replay/handoff/reset, fresh pose stationary time below 0.4 seconds, stale or faulted arm feedback, moving/pending/following grippers, and gripper requests without acknowledged post-request feedback. It permits a stationary controller STOP so the server can be reloaded while retaining its separate recovery instructions. Joint angles outside command limits do not turn this action into range recovery.

Before checking those conditions, the host freezes new control admission across both HTTP and gRPC. A failed check releases that freeze. An accepted restart keeps it until shutdown; emergency STOP remains available. No control commands, owners, enable requests, motion targets, or follower leases are replayed after restart.

State includes `backend_instance_id` and `backend_restart`:

- `supported`, `mode`: restart is available in `supervisor` mode or a directly session-owned `plugin` mode.
- `supervisor_process_id`, `supervisor_instance_id`: exact launcher identity supplied at startup.
- `plugin_owner`: immutable `--session-owner` identity for the plugin that launched a bundled host.
- `pending`: restart has been accepted and control admission is frozen.
- `releases_gripper_torque`, `preserves_arm_recovery`: both true.

A successful request returns HTTP/gRPC status **202** with the exact host and supervisor identities, `restart_mode`, `releases_gripper_torque:true`, and `recovery_preserved:true`. The host rechecks the idle conditions after a short response-drain period, then exits cleanly with reserved code **75**. A physical teach/motion change during that period cancels the restart, releases the admission freeze, and appears in `backend_restart.last_failure`.

`Start-WindowsDashboard.ps1` recognizes exit75 and starts one fresh monitoring-only process under its existing single-instance supervisor mutex. Ordinary `/api/shutdown` still exits with code 0 and retires the supervisor. In `plugin` mode there is no server self-spawn: the exact owning plugin must wait for its recorded child process to exit75, verify the backend ports are free, and then call its normal bundled-server startup once. Another plugin or an external client must not launch a replacement for that owned host.

A host started without either this supervisor or `--session-owner` returns **409** with manual launcher instructions and stays running. This endpoint never spawns an unrelated process, kills a process, changes installed files, or guesses a restart command.

## Failure evidence from 2026-11-06

The saved `stuck-live-state-20261106.json` shows a responsive host and fresh CAN feedback. The second arm has `recovery_required:true`, controller `arm_status:1`, and persisted current reason `Operator stop.` The retained stop event is `Unexpected joint movement during range entry.` At that event J3 measured +0.610° against −0.500° commanded, while J5 measured 4.070° against 3.446° commanded; recorded CAN error count was zero. Restarting the server does not resolve this physical controller STOP. Recovery must remain a separate, explicit arm workflow.

The first arm also retains an older historical stop event, but its active recovery latch is false. Diagnostic presentation must distinguish a historical stop record from an active STOP; the backend preserves both.

## Validation

`Piper.Host.Tests/BackendMaintenanceTests.cs` uses injected snapshots and callbacks, with no hardware or controller creation. It covers exact process/session identity, explicit jaw-release acknowledgment, concurrent request exclusion, failed-check rollback, one-shot restart commitment, physical movement during acknowledgement, STOP preservation, arm/teach/reset/fault/stale checks, Pika ownership/ACK/stationary checks, malformed inventories, direct plugin ownership, and unsupported supervision. `BackendRestartIntegrationTests.cs` verifies real HTTP and gRPC response receipt, token/owner/instance/ack guards, observer draining, and clean exit75 in the hardware-disabled native simulator, including a directly plugin-owned host. The existing host test still checks that ordinary shutdown ends an active gRPC observer and exits0. Never use physical robots as a maintenance test fixture.

## Updating an already running launcher

The currently running supervisor process retains the old script's arguments. Updating the files alone does not add restart supervision to that process. During an explicit maintenance window, with simulation following stopped and grippers empty, use the existing authenticated graceful `/api/shutdown`, wait for that exact `Piper.Host` PID and the old supervisor to exit normally, then launch the updated `Start-WindowsDashboard.ps1` once in a hidden PowerShell process. Do not kill either process or copy over loaded libraries. Confirm the new PID/session token and `backend_restart.mode:"supervisor"` afterward. Leave persisted `control-state.json` intact; a pre-existing STOP still needs its separate recovery flow. No live update is performed by the maintenance endpoint or its tests.

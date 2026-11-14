# Handoff: building a Unitree G1 plugin from what this repository learned

This document is for an agent (or person) starting a Lucky Engine plugin for the
Unitree G1 humanoid. It explains what this repository is, where it stands, what
to reuse, what not to reuse, and how to start. It was written on 2026-11-13,
when `main` contained everything through pull request #21.

Read this first, then `AGENTS.md`, then `docs/target-architecture.md`.

## 1. Decisions already made

- **The G1 plugin is a new repository, not a folder in this one.** This
  repository continues as the PiPER arm controller and will be worked on with
  real arms attached. Keep G1 work out of it.
- **Write the design before the code.** The first deliverable in the G1
  repository is its own `docs/target-architecture.md`: principles, component
  diagram, a state-machine spec, and a staged plan. This repository wrote that
  document late and paid for it with a long refactor.
- **Copy shared pieces first; extract a common library later.** Do not create a
  shared package until two plugins use the same code unchanged. Record where
  each copied file came from.
- **The architecture rule in `AGENTS.md` applies to the G1 repository too.**
  Copy it verbatim: build cleanly and modularly, never bolt a feature on, and
  stop and say so when a clean build needs a refactor first.

## 2. What this repository is

A controller for AgileX PiPER arms with Pika grippers, driven from Lucky Engine.

| Part | Path | Role |
|---|---|---|
| Native core (C) | `src/` | CAN bus, PiPER protocol, motion, the safety checks. Built by `Makefile`. |
| Native tests | `tests/*.c`, `tests/fake_bus.c` | Run against a fake bus; no hardware needed. |
| API definition | `managed/Piper.Contracts/piper-api.json` | The one schema for every Host request. |
| Generated API | `managed/Piper.Contracts/PiperApi.g.cs`, `web/` client | Produced by `tools/gen-api.mjs`; `make check-api` fails when stale. |
| Host | `managed/Piper.Host` | Process that owns the native core and serves HTTP and gRPC. |
| Gripper | `managed/Piper.Pika`, `managed/Piper.Pika.Agent` | Pika gripper sessions and the remote agent. |
| Processes | `managed/Piper.Processes` | `BoundedProcess`: run a child process with a deadline and bounded output. |
| Cameras | `managed/Piper.Camera`, `managed/Piper.RealSense` | Capture workers and lens calibration. |
| Engine plugin | `managed/Piper.Plugin` | The Lucky Engine plugin: UI, workflows, components. |
| Test kit | `managed/Testing/TestKit.cs` | The shared check helpers every managed test suite uses. |
| Dashboard | `web/` | Browser dashboard served by the Host. |
| CI | `.github/workflows/validate.yml` | Linux and Windows jobs; the full list of what must pass. |

## 2a. The engine side: Lucky Engine plugin support

A plugin is only half of the work. Lucky Engine itself had to learn to load
plugins, and that work is **not merged**. As of 2026-11-13:

| What | Where | State |
|---|---|---|
| Public native editor plugin API, plugin SDK and package manager | `luckyrobots/LuckyEngine` pull request 1099, branch `feature/plugin-sdk-pipers`, into `dev` | Open. Conflicts with `dev`. One check failing. |
| Native Pipers runtime integration (runtime robot, measured ghost, episode followers, viewport overlays) | Branch `checkpoint/office-pipers-20261106`, one commit on top of the *first* commit of the pull request branch | Pushed, but **not part of pull request 1099**. The pull request branch has since moved 102 commits ahead of where this branch started. |
| Plugin SDK the plugin builds against | `PluginSDK/` in the engine repository (`LuckyEngine.PluginSdk`, examples, tests) | CI here restores a pinned public preview of it; see `validate.yml`. |

What this means for a G1 plugin:

- Build against the plugin SDK from pull request 1099, and check that pull
  request's state before starting. If it has merged, use `dev`.
- Anything robot-specific that the engine must draw or evaluate natively was
  done for the PiPER in the checkpoint branch, and it is PiPER-shaped. Prefer
  keeping G1-specific behaviour in the plugin. If the engine needs a change,
  make it general (any robot), and propose it against the engine repository as
  its own pull request.
- Read `PluginSDK/README.md` on the pull request branch and
  `docs/lucky-engine-plugin.md` here for how the two sides meet.

## 3. The design that worked

These are the parts worth carrying to the G1 plugin. Each has a real example
here to read.

1. **One authority for motion permission.**
   `managed/Piper.Plugin/RobotCommandAuthority.cs` is the only owner of
   permission epochs. Every command that can move hardware asks it. Nothing
   else keeps its own "may I move" flag.
2. **STOP never waits.** The stop path takes no lock that a workflow can hold.
   See `managed/Piper.Plugin/EmergencyStop.cs` and, on the Host side,
   `CoordinatedStopAsync` in `managed/Piper.Host/HostControlDispatcher.cs`.
3. **Components own their state.** Each component file starts with a table:
   field, who writes it, what readers see. One writer per field. Readers get one
   immutable value through one volatile reference, updated by compare-and-swap.
   State changes are named transitions, not field assignments. Read
   `TelemetryComponent.cs` (smallest), then `FollowComponent.cs`, then
   `CalibrationComponent.cs` with its parts (`DeskCalibration.cs`,
   `SheetWizard.cs`, `HeightCheckSession.cs`, `LensCalibration.cs`,
   `CameraDiscovery.cs`).
4. **One API schema, generated on both sides.** The plugin builds every request
   through generated `PiperApi.Requests`; no hand-written JSON field names.
5. **Tests reach components through `internal` members.**
   `ComponentBoundaryTests` forbids private reflection in tests, which keeps
   the component boundary honest.
6. **Watchdogs do not depend on the hardware answering.** A heartbeat that the
   device itself must send cannot detect that the device is gone. See P4 in
   `docs/target-architecture.md`.
7. **Written state machines.** Section 3 of `docs/target-architecture.md` lists
   every machine with states, transitions and owner. Appendix B lists the tests
   each kind of machine owes.

## 4. What does not carry over

- **The native CAN core and the PiPER protocol.** The G1 is driven through
  Unitree's SDK over DDS, not raw CAN frames from this code.
- **Pika gripper code.** The G1's hands are different hardware.
- **The meaning of STOP.** On an arm, stopping means holding position or
  cutting power, and the arm stays on the desk. A humanoid that loses power or
  freezes mid-step falls. The G1 needs its own stop design: at least a damped
  mode, a balanced stand, and a clear rule for which one each fault leads to.
  Treat this as the first safety question, before any motion feature.
- **Joint count and modes.** The arm has six joints and a gripper. The G1 has
  many more joints plus balance and locomotion modes owned by Unitree's own
  controller. Decide early which layer the plugin commands (high-level
  locomotion, arm-only, or low-level joints) and what happens when the plugin
  and Unitree's controller disagree.

Confirm every G1 fact against Unitree's current documentation before relying on
it. Nothing in this section was tested on a G1.

## 5. Mistakes to avoid

- **Do not start with one large plugin class.** `PipersPlugin` here grew to 47
  partial files and about 8,800 lines before components were pulled out of it,
  and it is still not a thin shell. Start with components and a shell that only
  wires them.
- **Give the simulator a virtual clock from the first day.** The simulator here
  runs on real time. On a stalled CI runner, `tests/test_api.mjs` sees a gap in
  simulated status frames, the core treats it as an arm power cycle (correct
  safety behaviour), and the test fails. It failed five times; each time
  `gh run rerun <id> --failed` passed. A retry in the test would hide real
  faults, so it was left alone. A virtual clock removes the problem.
- **Build the view from one immutable snapshot.** Rendering here still reads
  live fields from many places, which is why it cannot be tested as a pure
  function.
- **Put the RPC path behind one interface early.** Workflows here call
  `Call`/`Command` on the plugin class directly, which ties them to it.

## 6. Where this repository stands

Stages are defined in section 4 of `docs/target-architecture.md`.

| Stage | State |
|---|---|
| 0 Cleanup | Done. |
| 1 Spec and characterization tests | Done, with gaps: no recorded trace files; machines M2 and T2 untested. |
| 2 Safety core rebuilt behind a typed C ABI | **Not started. Needs validation on real arms. Must not reach `main` without it.** |
| 3 One API schema with codegen | Plugin side done. Remaining: gRPC tunnel, confirmation IDs, action names still strings, `OperatorConfirmations.For` duplicates the schema's `x-piper-confirmations`. |
| 4 Plugin split into components | Components exist and own their state. Last two exit criteria unmet: the plugin class is not a pure shell and rendering is not pure. |
| 5 Pika heartbeat watchdog | Done. |
| 6 Delete old layers | Blocked behind Stage 2. |

Open items a later change should know about:

- Coordinated stop treats a native 409 from an emergency that is degraded but
  stopped as an arm-leg failure.
- `streamed_position_*` flags are dead.
- Pika `take_over` has no server-side check of the expected owner.
- `StopOwnedBackend` never kills the bundled Host when HTTP shutdown fails.
- A Host process that exits by itself is noticed only at the next start or stop.
- A second `Start` on a camera worker throws, where the spec says it is ignored.
- `SheetWizard.Edit` is a generic entry point; sheet answers are still a
  string-keyed dictionary.
- The faulted-camera-worker restart path in `CameraCapture.DisposeAsync` has no
  test.
- PowerShell tool edits under `tools/` were not executed. Nothing in this
  refactor was run against a real camera, WSL, or a real robot.

The next refactor recommended for Stage 4: give owners to the state the plugin
class still holds (preferences and selected profile, operator status lines,
motion approval and link state, scene binding), put the RPC path behind one
interface, then move workflows into components and build the view from one
snapshot.

## 7. Working in this repository

- Native: `make test` (Linux; CI uses `NO_SKIPS=1`), `make test-windows`.
- API and dashboard: `make check-api test-web`.
- Managed: the exact `dotnet` commands are the steps in
  `.github/workflows/validate.yml`; run those rather than guessing.
  `NativeAdmissionTests` skips unless `PIPER_TEST_NATIVE_PATH` is set.
- Before committing, discard the local change the restore makes:
  `git checkout -- managed/Piper.Camera/packages.lock.json`.
- Files use LF line endings.
- Every pull request is reviewed by Cursor Bugbot. Read its comments
  (`gh api repos/luckyrobots/piper-direct-c/pulls/<n>/comments`) and wait for
  all checks before merging. Merge with a merge commit.

## 8. Suggested first steps for the G1 repository

1. Create the repository; copy `AGENTS.md` and `CLAUDE.md`.
2. Read Unitree's G1 SDK documentation and write down: control layers, modes,
   what the built-in controller does on loss of commands, and the emergency
   behaviours available.
3. Write `docs/target-architecture.md`: the stop and fault design first, then
   components, then the API schema, then the staged plan.
4. Build a simulator with a virtual clock and a fake transport, and the test
   kit (copy `managed/Testing/TestKit.cs`).
5. Build the motion authority and the stop path, with tests, before any
   feature that moves the robot.
6. Add the API schema and generator (copy `tools/gen-api.mjs` and adapt), then
   the Host, then plugin components one at a time.
7. Set up CI on Linux and Windows from `validate.yml`.
8. Do not run anything on a real G1 until the owner is present and has agreed
   to the stop design.

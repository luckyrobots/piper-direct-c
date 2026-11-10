# Multiple Pika grippers

Each assigned Pika has a separate Linux agent, serial connection, owner lease,
follow session and power-request evidence. Changing the selected robot does not
disconnect another robot's gripper. Never open the CH340/WCH Windows COM driver.

## Discover and assign

`GET /api/pika/devices?distribution=Ubuntu` reads usbipd-win and Linux sysfs only.
The equivalent authenticated gRPC `Execute` request uses route
`/api/pika/devices` and JSON `{ "pika_distribution": "Ubuntu" }`.

The response contains `devices`, with `identity` (Windows USB instance ID),
`name`, `device`, `distribution`, `bus_id`, `shared`, `attached`, `available`,
`connected`, `owner`, `aliases`, and an actionable `message`. Devices not yet
shared with WSL remain visible in this list. Discovery does not bind, attach,
open a serial port, enable a motor or send a position target.

On Windows persist the returned `device` and `distribution` in the robot's
profile. A device such as `/dev/pika/by-host/11-4` identifies the host USBIP
bus/port; the agent resolves it to the current Linux tty at connection time.
On native Linux the identity is the udev `by-path` link or, without one,
`/dev/pika/by-port/<controller>+port-<chain>` read from sysfs. An adapter whose
USB port cannot be read is not listed: a tty name is never an identity.
If the gripper is moved to another USB port, explicitly reassign it in Settings.
Do not identify two serial-number-free CH340 grippers by `/dev/ttyUSB0` order
or their identical `/dev/serial/by-id/usb-1a86_USB_Serial-if00-port0` name.
First-time ambiguous by-id assignments are rejected; an already verified alias
remains bound to its original device for that backend session.

The mapping follows usbipd-win's exported USB device identity: bus number is
`BusId.Bus`, device number is `BusId.Port`; Linux `vhci_hcd` exposes this remote
device ID alongside its local USB bus ID. See
[usbipd-win ExportedDevice](https://github.com/dorssel/usbipd-win/blob/master/Usbipd/ExportedDevice.cs).
The bundled agent's `--list-devices` mode only enumerates this metadata.

## One-time Windows USB setup

Run the bundled `tools/Setup-PikaUsb.ps1` in Administrator PowerShell with the
exact `windows_instance_id` returned by discovery and the chosen distribution:

```powershell
./tools/Setup-PikaUsb.ps1 -InstanceId 'USB\VID_1A86&PID_7522\EXACT_INSTANCE' -Distribution Ubuntu
```

This checks the exact CH340K instance before binding only that device, then
attaches it to WSL. It does not use COM, open serial or command the gripper.
Existing already-shared devices are not rebound. The standalone dashboard
supervisor reattaches all already-shared Pika adapters after disconnects, rather
than requiring there to be exactly one Pika. The installer must not share CAN,
cameras or unrelated serial devices through this script.

For the standalone supervisor, an explicitly supplied `-PikaDevice` takes
precedence. Otherwise it reads `data-windows-managed/pika-default.json`, for
example `{ "device": "/dev/pika/by-host/11-4", "distribution": "Ubuntu" }`.
Without a saved assignment it selects a canonical host identity only when
exactly one already-shared Pika is present; it never guesses between two units.
The plugin keeps its assignments in its own per-robot preferences instead.

## Route commands explicitly

All Pika mutations retain their existing owner and power guards. Add these
selectors to every `/api/pika/*` command and coordinated stop:

```json
{
  "owner": "dashboard-owner-uuid",
  "pika_device": "/dev/pika/by-host/11-4",
  "pika_distribution": "Ubuntu"
}
```

These selectors are independent of the arm's CAN `device_id`. `configure` also
accepts the assignment's `device` and `distribution`, registers that independent
session and opens no port. `connect` opens only the explicitly assigned Pika in
read-only mode; motor enable remains a separate guarded action. The same
physical host device cannot acquire an active second session through a different
WSL distribution. Bundled agents also hold an exclusive resolved-tty lock to
avoid opening the same physical gripper under two aliases.

`/api/state` and gRPC state frames contain `pikas`, an array of independent
snapshots with canonical `device`, `distribution` and known `aliases`. The
legacy `pika` property and selector-free commands address the CLI default
assignment only. A plugin must project the selected profile's matching snapshot
from `pikas`; it must never reuse a different robot's `pika` telemetry as fallback.
`GET /api/pika/state?pika_device=...&pika_distribution=...` returns one selected
snapshot, including its passive diagnostic evidence.

An exited passive monitor may retain an obsolete plugin owner until its next
connection. Its snapshot advertises `read_only_adoptable: true` only when the
backend has privately confirmed process exit, no outstanding power request or
uncertainty, no pending operation, and disabled/unarmed/nonfollowing feedback.
The new plugin can reconnect that exact device with `take_over: true` and
`read_only_adopt: true`. The backend rechecks those private conditions under its
serialized operation lock before changing ownership. A copied or stale public
capability cannot steal a newly connected or powered session. This reconnect
opens a read-only monitor; it sends no enable or position command. Older
backends without the attestation are not automatically taken over.

## Verification

`Piper.Pika.Tests` checks independent owners, serial targets, delayed ACKs,
failure evidence, disconnects, immutable alias binding, cross-distribution
admission, invalid selectors, and unshared-device inventory. Existing per-agent
tests retain ownership and causally verified power-state checks. Host tests
check matching HTTP/gRPC inventory behavior and hardware-disabled simulation.
The discovery-only Linux agent has also been checked against live sysfs without
opening a serial port. Concurrent hardware connections require the separately
authorized USB setup and a read-only two-agent telemetry test before motion.

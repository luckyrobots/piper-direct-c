"""Developer-only, explicitly executed J6 PV/JS comparison. Python stdlib only.

No --execute: read-only preflight. --self-test: no network or robot access.
--execute: three identical 0/+12/0 degree wrist cycles in each transport.
Never commands Pika, resets faults, disables motors or performs recovery.
"""
import argparse
import copy
import json
import math
from pathlib import Path
import statistics
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timezone

AMPLITUDE_DEGREES = 12.0
LEG_SECONDS = 2.0
DWELL_SECONDS = 0.4
REPETITIONS = 3
PERIOD_SECONDS = 0.030
MAX_INPUT_GAP_SECONDS = 0.200
PV = "position-velocity"
JS = "streamed-position"


def quintic(u):
    u = min(1.0, max(0.0, u))
    return u * u * u * (10.0 + u * (-15.0 + 6.0 * u))


def next_deadline(previous, now, period=PERIOD_SECONDS):
    """Skip missed slots, never publish a burst of expired targets."""
    due = previous + period
    if due < now:
        due += math.ceil((now - due) / period) * period
    return due


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def finite(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def percentile(values, fraction):
    values = sorted(values)
    return values[min(len(values) - 1, round((len(values) - 1) * fraction))] if values else None


def redacted(value, secrets=()):
    if isinstance(value, dict):
        return {key: "[redacted]" if "token" in key.lower() or "owner" in key.lower() or "session" in key.lower()
                else redacted(item, secrets) for key, item in value.items()}
    if isinstance(value, list):
        return [redacted(item, secrets) for item in value]
    if isinstance(value, str):
        for secret in secrets:
            if secret:
                value = value.replace(secret, "[redacted]")
    return value


class Comparison:
    def __init__(self, base_url, device_id, owner, output):
        uri = urllib.parse.urlparse(base_url)
        require(uri.scheme == "http" and uri.hostname in ("127.0.0.1", "localhost", "::1") and
                not uri.username and not uri.password and uri.path in ("", "/") and not uri.query and not uri.fragment,
                "Only an explicit loopback HTTP backend is allowed.")
        require(16 <= len(owner) <= 80, "Owner must be 16–80 characters.")
        self.base_url, self.device_id, self.owner, self.output = base_url.rstrip("/"), device_id, owner, Path(output)
        self.token = ""
        self.generation = None
        self.limit_signature = None
        self.initial_degrees = None
        self.pika_position = None
        self.pika_owner = None
        self.pika_enabled = None
        self.preflight_state = None
        self.firmware = None
        self.request_id = None
        self.pending_admission = False
        self.before_admission_request = 0
        self.sequence = 0
        self.transport = None
        self.last_update = None
        self.began = time.monotonic()
        self.rows = []
        self.receipt = dict(kind="piper.j6-pv-js-comparison", startedUtc=datetime.now(timezone.utc).isoformat(),
                            executed=False, passed=False, preflightPassed=False, repetitions=REPETITIONS, amplitudeDegrees=AMPLITUDE_DEGREES,
                            legSeconds=LEG_SECONDS, minimumDwellSeconds=DWELL_SECONDS, periodSeconds=PERIOD_SECONDS,
                            requestedSpeedPercent=100, samples=self.rows, requests=[],
                            limitations=["Receipt-time telemetry is not a firmware sample clock.",
                                         "Velocity/acceleration variation is descriptive, not a certified physical jerk measurement.",
                                         "This small J6 comparison does not qualify a full pickup or change any native limits."])

    def get(self):
        with urllib.request.urlopen(self.base_url + "/api/state", timeout=.2) as response:
            state = json.load(response)
        require(isinstance(state, dict), "Backend returned no state object.")
        if self.token:
            require(state.get("token") == self.token, "Backend identity changed; no target may resume.")
        return state

    def post(self, action, extra=None):
        require(action in ("follow-start", "follow-update", "follow-end"), "The comparison permits scoped following commands only.")
        body = dict(action=action, device_id=self.device_id, generation=self.generation, owner=self.owner,
                    standalone=True, clear=True, supported=True, speed_percent=100, tolerance_deg=.25)
        if self.request_id is not None:
            body["request_id"] = self.request_id
        body.update(extra or {})
        request = urllib.request.Request(self.base_url + "/api/command", data=json.dumps(body).encode(), method="POST",
                                         headers={"Content-Type": "application/json", "X-Piper-Token": self.token})
        sent_at = time.monotonic()
        entry = dict(action=action, seconds=sent_at - self.began, submitted=redacted(copy.deepcopy(body), (self.token, self.owner)))
        try:
            with urllib.request.urlopen(request, timeout=.2) as response:
                result = json.load(response)
            entry["acknowledgement"] = redacted(copy.deepcopy(result), (self.token, self.owner))
        except urllib.error.HTTPError as error:
            detail = error.read().decode("utf-8", "replace")
            entry["error"] = redacted(detail, (self.token, self.owner))
            raise RuntimeError(f"{action} rejected ({error.code}): {redacted(detail, (self.token, self.owner))}") from None
        except BaseException as error:
            entry["error"] = redacted(str(error), (self.token, self.owner))
            raise
        finally:
            entry["requestSeconds"] = time.monotonic() - sent_at
            self.receipt["requests"].append(entry)
        require(result.get("ok", True) is True, action + " was not acknowledged.")
        return result

    def device(self, state):
        require(state.get("selected") == self.device_id, "The selected CAN adapter changed.")
        found = [item for item in state.get("devices", []) if item.get("id") == self.device_id]
        require(len(found) == 1, "The explicitly selected robot is absent or ambiguous.")
        return found[0]

    def health(self, state, active=False, idle=False):
        device = self.device(state)
        require(device.get("generation") == self.generation, "The CAN connection generation changed.")
        require(device.get("present") is True and device.get("up") is True and device.get("fresh") is True,
                "Arm feedback or the CAN connection is not fresh.")
        require(not device.get("recovery_required") and not device.get("reset_pending"), "Recovery or reset is pending.")
        status = device.get("status", {})
        require(status.get("error") == 0 and status.get("arm_status") == 0 and status.get("control") == 1 and
                status.get("motion_mode") == 1 and status.get("teach_state") in (0, 2, 6),
                "Controller mode/fault/teaching state changed.")
        require(finite(status.get("age_s")) and 0 <= status["age_s"] <= .150, "Controller status is too old.")
        require((device.get("hardware"), device.get("firmware")) == self.firmware, "Firmware identity changed.")
        joints = device.get("joints", [])
        require(len(joints) == 6 and [joint.get("index") for joint in joints] == list(range(1, 7)), "Six ordered joints are required.")
        for joint in joints:
            require(joint.get("fresh") is True and joint.get("enabled") is True and joint.get("fault") == 0 and
                    finite(joint.get("age_s")) and 0 <= joint["age_s"] <= .150 and
                    joint.get("limits_fresh") is True and joint.get("acceleration_fresh") is True,
                    "A joint lost enabled/fresh/fault-free feedback or reported limits.")
            require(all(finite(joint.get(key)) for key in ("actual_deg", "min_deg", "max_deg", "max_speed_rad_s", "max_acceleration_rad_s2")),
                    "Joint feedback or limits are not finite.")
        require(self.limits(joints) == self.limit_signature, "Reported physical limits changed.")
        require(all(abs(joints[index]["actual_deg"] - self.initial_degrees[index]) <= .5 for index in range(5)),
                "A joint other than J6 moved by more than 0.5 degrees.")
        pika = state.get("pika", {})
        position = pika.get("telemetry", {}).get("motor", {}).get("Position")
        require(pika.get("fresh") is True and isinstance(self.pika_enabled, bool) and pika.get("enabled") is self.pika_enabled and not pika.get("driver_fault") and
                not pika.get("following") and not pika.get("pending") and pika.get("owner") == self.pika_owner and
                finite(position) and abs(position - self.pika_position) <= .05,
                "Pika ownership, jaw position, enabled state or fresh feedback changed; no jaw command is permitted.")
        motion = state.get("motion", {})
        if active:
            require(motion.get("active") is True and motion.get("following") is True and motion.get("owner") == self.owner and
                    motion.get("request_id") == self.request_id, "The admitted following scope is no longer active.")
            require(motion.get("trajectory_transport", PV) == self.transport, "The native transport changed.")
            require(finite(motion.get("follow_sequence")) and 0 <= motion["follow_sequence"] <= self.sequence,
                    "Native following sequence does not belong to the issued samples.")
        if idle:
            require(motion.get("active") is False and not motion.get("following"), "Another action is active.")
        return device

    @staticmethod
    def limits(joints):
        return tuple(tuple(joint.get(key) for key in ("min_deg", "max_deg", "max_speed_rad_s", "max_acceleration_rad_s2")) for joint in joints)

    def preflight(self):
        try:
            state = self._preflight()
            self.receipt["preflightPassed"] = True
            self.save()
            return state
        except BaseException as error:
            self.receipt["failureStage"] = "preflight"
            self.receipt["failure"] = redacted(str(error), (self.token, self.owner, self.pika_owner))
            if self.preflight_state is not None:
                self.receipt["failureState"] = redacted(copy.deepcopy(self.preflight_state), (self.token, self.owner, self.pika_owner))
            self.save()
            raise

    def _preflight(self):
        state = self.get()
        self.preflight_state = state
        require(isinstance(state.get("token"), str) and state["token"], "Backend token is unavailable.")
        self.token = state["token"]
        device = self.device(state)
        self.generation = device.get("generation")
        self.firmware = device.get("hardware"), device.get("firmware")
        require(state.get("trajectory_follow_supported") is True and state.get("streamed_position_transport_supported") is True and
                state.get("streamed_position_wire_validation_supported") is True and
                device.get("streamed_position_supported") is True and self.firmware[0] == "H-V1.2-1" and
                self.firmware[1] in ("S-V1.9-0", "S-V1.9-1"), "The reviewed opt-in transport backend/firmware is not loaded.")
        joints = device.get("joints", [])
        require(len(joints) == 6 and all(finite(joint.get("actual_deg")) for joint in joints), "No six-joint starting pose.")
        self.initial_degrees = [joint["actual_deg"] for joint in joints]
        require(all(abs(angle) <= .25 for angle in self.initial_degrees), "Start with all six joints already at zero; this harness does not home or recover.")
        self.limit_signature = self.limits(joints)
        pika = state.get("pika", {})
        self.pika_owner = pika.get("owner")
        self.pika_position = pika.get("telemetry", {}).get("motor", {}).get("Position")
        self.pika_enabled = pika.get("enabled")
        require(isinstance(self.pika_enabled, bool), "Pika must report its enabled/disabled state before the comparison.")
        self.receipt["pikaBaselineEnabled"] = self.pika_enabled
        self.health(state, idle=True)
        require(device.get("status", {}).get("stationary_s", 0) >= .3, "Wait for measured stationary zero before admission.")
        require(all(joint["min_deg"] <= self.initial_degrees[index] <= joint["max_deg"] for index, joint in enumerate(joints)),
                "The captured holding pose is outside reported joint limits.")
        wrist = joints[5]
        require(wrist["min_deg"] <= 0 <= AMPLITUDE_DEGREES <= wrist["max_deg"], "The J6 comparison exceeds reported position limits.")
        require(AMPLITUDE_DEGREES * 1.875 / LEG_SECONDS <= wrist["max_speed_rad_s"] * 180 / math.pi and
                AMPLITUDE_DEGREES * (10 / math.sqrt(3)) / (LEG_SECONDS * LEG_SECONDS) <= wrist["max_acceleration_rad_s2"] * 180 / math.pi,
                "The prescribed quintic exceeds reported J6 speed/acceleration limits.")
        self.capture(state, "preflight", 0, 0, 0)
        self.receipt.update(deviceId=self.device_id, generation=self.generation, firmware=list(self.firmware),
                            fixedOtherJointDegrees=self.initial_degrees[:5], initialJ6Degrees=self.initial_degrees[5],
                            reportedLimits=self.limit_signature)
        return state

    def capture(self, state, phase, elapsed, start, end):
        self.rows.append(dict(seconds=time.monotonic() - self.began, utc=datetime.now(timezone.utc).isoformat(),
                              transport=self.transport, phase=phase, phaseElapsedSeconds=elapsed,
                              legStartDegrees=start, legEndDegrees=end,
                              plannedJ6Degrees=start + (end - start) * quintic(elapsed / LEG_SECONDS),
                              state=redacted(copy.deepcopy(state), (self.token, self.owner, self.pika_owner))))

    def start(self, transport, trajectory=True):
        snapshot = self.get()
        self.health(snapshot, idle=True)
        self.transport = transport
        fields = {"trajectory": True, "trajectory_transport": transport} if trajectory else {}
        self.before_admission_request = snapshot.get("motion", {}).get("request_id", 0)
        self.pending_admission = True
        sent_at = time.monotonic()
        result = self.post("follow-start", fields)
        self.request_id = result.get("request_id")
        require(finite(self.request_id) and self.request_id > 0, "Native admission omitted its request identity.")
        self.pending_admission = False
        require(result.get("trajectory_transport", PV) == transport and (not trajectory or result.get("trajectory_follow") is True) and
                (transport != JS or result.get("trajectory_transport_validated") is True),
                "Native admission did not acknowledge the requested transport/profile.")
        self.sequence = 0
        self.last_update = sent_at

    def sample(self, phase, elapsed, start, end, target):
        state = self.get()
        self.capture(state, phase, elapsed, start, end)
        device = self.health(state, active=True)
        require(time.monotonic() - self.last_update < MAX_INPUT_GAP_SECONDS,
                "Physical publication gap exceeded 200 ms; no delayed target will be replayed.")
        degrees = self.initial_degrees[:5] + [target]
        require(all(finite(value) and bounds[0] <= value <= bounds[1] for value, bounds in zip(degrees, self.limit_signature)),
                "A generated target is outside the pinned reported position bounds.")
        self.sequence += 1
        sent_at = time.monotonic()
        self.post("follow-update", dict(sequence=self.sequence, degrees=degrees))
        self.last_update = sent_at
        return device

    def leg(self, ordinal, start, end):
        began = due = time.monotonic()
        while True:
            elapsed = time.monotonic() - began
            if elapsed >= LEG_SECONDS:
                break
            self.sample(f"leg-{ordinal}", elapsed, start, end, start + (end - start) * quintic(elapsed / LEG_SECONDS))
            due = next_deadline(due, time.monotonic())
            time.sleep(max(0, due - time.monotonic()))
        self.settle(f"dwell-{ordinal}", end, DWELL_SECONDS)

    def settle(self, phase, target, minimum_seconds):
        began = due = time.monotonic()
        while True:
            elapsed = time.monotonic() - began
            device = self.sample(phase, LEG_SECONDS, target, target, target)
            settled = abs(device["joints"][5]["actual_deg"] - target) <= .25 and device["status"].get("stationary_s", 0) >= .3
            if elapsed >= minimum_seconds and settled:
                return
            require(elapsed <= 5, "J6 did not settle at its endpoint; no next leg was started.")
            due = next_deadline(due, time.monotonic())
            time.sleep(max(0, due - time.monotonic()))

    def end(self):
        self.post("follow-end")
        self.request_id = None
        began = time.monotonic()
        while True:
            state = self.get()
            self.receipt["finalHoldState"] = redacted(copy.deepcopy(state), (self.token, self.owner, self.pika_owner))
            device = self.health(state, idle=True)
            self.capture(state, "released-zero-hold", LEG_SECONDS, 0, 0)
            require(all(abs(joint["actual_deg"]) <= .25 for joint in device["joints"]), "Released hold is no longer at zero.")
            if time.monotonic() - began >= .4 and device["status"].get("stationary_s", 0) >= .3:
                return
            time.sleep(PERIOD_SECONDS)

    def cleanup(self):
        """Only retire this exact live follow scope; never reset/disable/recover."""
        if self.request_id is None and not self.pending_admission:
            return "No admitted request remains."
        try:
            state = self.get()
            self.receipt["cleanupState"] = redacted(copy.deepcopy(state), (self.token, self.owner, self.pika_owner))
            device = self.device(state)
            motion = state.get("motion", {})
            if device.get("generation") != self.generation:
                return "The connection generation changed; no hold was sent to a replacement connection."
            if device.get("recovery_required") or device.get("status", {}).get("arm_status") or device.get("status", {}).get("error"):
                return "Controller already faulted/stopped; no recovery or mode transition attempted."
            if self.request_id is None and self.pending_admission and motion.get("active") and motion.get("following") and motion.get("owner") == self.owner:
                observed_request = motion.get("request_id")
                if finite(observed_request) and observed_request > self.before_admission_request:
                    self.request_id = observed_request
            if motion.get("active") and motion.get("following") and motion.get("owner") == self.owner and motion.get("request_id") == self.request_id:
                self.post("follow-end")
                return "Scoped follow-end requested; no reset, power change or recovery."
            return "The request already retired or scope changed; no new command sent."
        except Exception as error:
            return "Scoped hold could not be verified: " + str(redacted(str(error), (self.token, self.owner)))

    def run(self):
        self.receipt["executed"] = True
        try:
            for transport in (PV, JS):
                self.start(transport)
                self.settle("initial-zero-hold", 0, .4)
                for repeat in range(REPETITIONS):
                    self.leg(2 * repeat + 1, 0, AMPLITUDE_DEGREES)
                    self.leg(2 * repeat + 2, AMPLITUDE_DEGREES, 0)
                self.end()
            # A new ordinary follow admission is a same-position PV hold,
            # after JS has ended and fresh encoders prove stationary zero.
            self.start(PV, trajectory=False)
            self.settle("ordinary-pv-stationary-handoff", 0, .5)
            self.end()
            self.receipt["passed"] = True
        except BaseException as error:
            self.receipt["failureStage"] = "physical-comparison"
            self.receipt["failure"] = redacted(str(error), (self.token, self.owner))
            self.receipt["cleanup"] = self.cleanup()
            raise
        finally:
            self.save()

    def save(self):
        self.receipt["finishedUtc"] = datetime.now(timezone.utc).isoformat()
        self.receipt["comparison"] = summarize(self.rows, self.device_id)
        self.output.parent.mkdir(parents=True, exist_ok=True)
        self.output.write_text(json.dumps(redacted(self.receipt, (self.token, self.owner, self.pika_owner)), indent=2), encoding="utf-8")


def summarize(rows, device_id):
    results = {}
    for transport in (PV, JS):
        selected = [row for row in rows if row["transport"] == transport and row["phase"].startswith("leg-")]
        errors, plan_errors, velocities, timed_velocity = [], [], [], []
        delays = []
        for phase in sorted({row["phase"] for row in selected}):
            phase_rows = [row for row in selected if row["phase"] == phase]
            if len(phase_rows) >= 10:
                fits = []
                for delay_index in range(101):
                    delay = delay_index * .005
                    squared = []
                    for row in phase_rows:
                        device = next(item for item in row["state"]["devices"] if item["id"] == device_id)
                        expected = row["legStartDegrees"] + (row["legEndDegrees"] - row["legStartDegrees"]) * quintic((row["phaseElapsedSeconds"] - delay) / LEG_SECONDS)
                        squared.append((device["joints"][5]["actual_deg"] - expected) ** 2)
                    fits.append((statistics.mean(squared), delay))
                delays.append(min(fits)[1])
        for row in selected:
            device = next(item for item in row["state"]["devices"] if item["id"] == device_id)
            joint = device["joints"][5]
            actual, command, velocity = joint["actual_deg"], joint.get("command_deg"), joint.get("velocity_rad_s")
            plan_errors.append(abs(actual - row["plannedJ6Degrees"]))
            if finite(command):
                errors.append(abs(command - actual))
            if finite(velocity):
                degrees_velocity = velocity * 180 / math.pi
                velocities.append(abs(degrees_velocity))
                timed_velocity.append((row["seconds"], row["phase"], degrees_velocity))
        accelerations = [(later[2] - earlier[2]) / (later[0] - earlier[0])
                         for earlier, later in zip(timed_velocity, timed_velocity[1:])
                         if later[1] == earlier[1] and .005 <= later[0] - earlier[0] <= .100]
        results[transport] = dict(samples=len(selected), nativeTrackingMaxDegrees=max(errors, default=None),
                                  nativeTrackingP95Degrees=percentile(errors, .95), plannedLagP95Degrees=percentile(plan_errors, .95),
                                  fittedPlanDelayMedianSeconds=statistics.median(delays) if delays else None,
                                  perLegFittedPlanDelaySeconds=delays, measuredMotorVelocityPeakDegreesPerSecond=max(velocities, default=None),
                                  receiptTimeAccelerationRmsDegreesPerSecondSquared=math.sqrt(statistics.mean(value * value for value in accelerations)) if accelerations else None)
    return results


def self_test():
    require(quintic(0) == 0 and quintic(1) == 1 and quintic(-1) == 0 and quintic(2) == 1, "Quintic endpoints/clamps failed.")
    samples = [quintic(index / 2000) for index in range(2001)]
    require(all(later >= earlier for earlier, later in zip(samples, samples[1:])), "Quintic reverses direction.")
    require(abs((quintic(.500001) - quintic(.499999)) / .000002 - 1.875) < 1e-7, "Quintic peak velocity changed.")
    require(abs(next_deadline(0, .005) - .030) < 1e-10 and abs(next_deadline(0, .095) - .120) < 1e-10,
            "ACK time was added or missed slots were replayed.")
    safe = redacted({"token": "secret", "motion": {"owner": "owner-secret"}, "error": "secret owner-secret"}, ("owner-secret", "secret"))
    require("secret" not in json.dumps(safe), "Token/owner redaction failed.")
    require(summarize([], "none")[PV]["samples"] == 0, "Empty comparison became a successful measurement.")
    print("J6 comparison self-tests passed; no network or robot commands were used.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:8765")
    parser.add_argument("--device-id", default="usb:004000485246570520323934")
    parser.add_argument("--owner", default=uuid.uuid4().hex, help="Explicit backend session owner if required; never written to the receipt.")
    parser.add_argument("--output", default="outputs/lucky-engine/j6-pv-js-comparison-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + ".json")
    parser.add_argument("--execute", action="store_true", help="Perform the bounded physical comparison after preflight.")
    parser.add_argument("--self-test", action="store_true", help="Run pure offline checks without accessing the backend.")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    comparison = Comparison(args.base_url, args.device_id, args.owner, args.output)
    try:
        comparison.preflight()
        if not args.execute:
            print(json.dumps(dict(preflight="passed; read-only", executeRequired=True, device=args.device_id, output=str(comparison.output.resolve()),
                                  motion="PV then JS: 3 repetitions each, J6 zero to +12 degrees to zero, 2-second quintic legs, >=0.4-second dwells",
                                  otherJoints="captured fixed", gripper="unchanged; no Pika writes", onFailure="scoped follow-end only; never reset/disable/recover"), indent=2))
            return 0
        comparison.run()
        print(json.dumps(dict(passed=True, output=str(comparison.output.resolve()), comparison=comparison.receipt["comparison"]), indent=2))
        return 0
    except BaseException as error:
        print(str(redacted(str(error), (comparison.token, comparison.owner))), file=sys.stderr)
        print("Comparison receipt: " + str(comparison.output.resolve()), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

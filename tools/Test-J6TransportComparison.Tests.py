"""Offline contract tests; never opens a network or hardware connection."""
import copy
import importlib.util
import io
import json
import math
from pathlib import Path
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location("comparison", Path(__file__).with_name("Test-J6TransportComparison.py"))
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)


class Clock:
    def __init__(self):
        self.value = 10.0

    def monotonic(self):
        return self.value

    def sleep(self, seconds):
        assert seconds >= 0
        self.value += seconds


class FakeComparison(comparison.Comparison):
    def __init__(self, output, clock, fault=False, wrong_ack=False, lost_ack=False, enable_flip=False):
        super().__init__("http://127.0.0.1:8765", "can-fixture", "fixture-owner-secret", output)
        self.clock, self.fault, self.wrong_ack = clock, fault, wrong_ack
        self.lost_ack = lost_ack
        self.enable_flip = enable_flip
        self.actions = []
        self.updates = 0
        self.counter = 100
        self.last_post = clock.monotonic()
        self.board = dict(token="fixture-token-secret", selected="can-fixture", trajectory_follow_supported=True,
                          streamed_position_transport_supported=True,
                          streamed_position_wire_validation_supported=True,
                          motion=dict(active=False, following=False, owner="", request_id=0, follow_sequence=0),
                          devices=[dict(id="can-fixture", generation=7, hardware="H-V1.2-1", firmware="S-V1.9-0",
                                        streamed_position_supported=True, present=True, up=True, fresh=True,
                                        recovery_required=False, reset_pending=False,
                                        status=dict(error=0, arm_status=0, control=1, motion_mode=1, teach_state=0, age_s=.001, stationary_s=1),
                                        joints=[dict(index=index + 1, actual_deg=0., command_deg=0., velocity_rad_s=0.,
                                                     fresh=True, enabled=True, fault=0, age_s=.001,
                                                     limits_fresh=True, acceleration_fresh=True,
                                                     min_deg=-180., max_deg=180., max_speed_rad_s=3., max_acceleration_rad_s2=5.)
                                                for index in range(6)])],
                          pika=dict(owner="pika-owner-secret", fresh=True, enabled=True, driver_fault=False,
                                    following=False, pending=False, telemetry=dict(motor=dict(Position=2.8))))

    def get(self):
        self.clock.value += .002
        return copy.deepcopy(self.board)

    def post(self, action, extra=None):
        assert action in ("follow-start", "follow-update", "follow-end")
        extra = extra or {}
        self.actions.append((action, copy.deepcopy(extra)))
        self.clock.value += .002
        motion = self.board["motion"]
        if action == "follow-start":
            assert not motion["active"]
            self.counter += 1
            mode = extra.get("trajectory_transport", comparison.PV)
            motion.update(active=True, following=True, owner=self.owner, request_id=self.counter,
                          trajectory_transport=mode, follow_sequence=0)
            if self.lost_ack:
                raise TimeoutError("Fixture admitted following but lost its acknowledgement")
            return dict(ok=True, request_id=self.counter, trajectory_follow=extra.get("trajectory", False),
                        trajectory_transport_validated=mode == comparison.JS,
                        trajectory_transport=comparison.PV if self.wrong_ack and mode == comparison.JS else mode)
        assert motion["active"] and motion["request_id"] == self.request_id and motion["owner"] == self.owner
        if action == "follow-end":
            motion.update(active=False, following=False, owner="")
        else:
            assert extra["sequence"] > motion["follow_sequence"]
            motion["follow_sequence"] = extra["sequence"]
            elapsed = self.clock.monotonic() - self.last_post
            for joint, angle in zip(self.board["devices"][0]["joints"], extra["degrees"]):
                joint["velocity_rad_s"] = (angle - joint["actual_deg"]) * math.pi / 180 / elapsed
                joint["actual_deg"] = joint["command_deg"] = angle
            self.updates += 1
            if self.enable_flip and self.updates == 5:
                self.board["pika"]["enabled"] = not self.board["pika"]["enabled"]
            if self.fault and self.updates == 5:
                self.board["devices"][0]["status"]["error"] = 1
                self.board["devices"][0]["recovery_required"] = True
                motion.update(active=False, following=False, owner="")
        self.last_post = self.clock.monotonic()
        return dict(ok=True)


class HarnessTests(unittest.TestCase):
    def test_complete_two_transport_contract_and_pv_handoff(self):
        clock = Clock()
        with tempfile.TemporaryDirectory(prefix="piper-j6-contract-") as temporary, mock.patch.object(comparison, "time", clock):
            fixture = FakeComparison(Path(temporary) / "receipt.json", clock)
            fixture.preflight()
            fixture.run()
            starts = [fields for action, fields in fixture.actions if action == "follow-start"]
            self.assertEqual(starts, [dict(trajectory=True, trajectory_transport=comparison.PV),
                                      dict(trajectory=True, trajectory_transport=comparison.JS), {}])
            for transport in (comparison.PV, comparison.JS):
                self.assertEqual({row["phase"] for row in fixture.rows if row["transport"] == transport and row["phase"].startswith("leg-")},
                                 {"leg-" + str(index) for index in range(1, 7)})
            targets = [fields["degrees"] for action, fields in fixture.actions if action == "follow-update"]
            self.assertTrue(all(target[:5] == [0.] * 5 and 0 <= target[5] <= 12 for target in targets))
            self.assertEqual(targets[-1], [0.] * 6)
            text = fixture.output.read_text(encoding="utf-8")
            self.assertNotIn("secret", text)
            self.assertTrue(json.loads(text)["passed"])
            self.assertFalse(fixture.board["motion"]["active"])

    def test_fault_never_resets_or_replays(self):
        clock = Clock()
        with tempfile.TemporaryDirectory(prefix="piper-j6-contract-") as temporary, mock.patch.object(comparison, "time", clock):
            fixture = FakeComparison(Path(temporary) / "receipt.json", clock, fault=True)
            fixture.preflight()
            with self.assertRaises(RuntimeError):
                fixture.run()
            self.assertEqual(fixture.updates, 5)
            self.assertNotIn("follow-end", [action for action, _ in fixture.actions])
            saved = json.loads(fixture.output.read_text(encoding="utf-8"))
            self.assertFalse(saved["passed"])
            self.assertTrue(saved["cleanupState"]["devices"][0]["recovery_required"])

    def test_disabled_gripper_baseline_stays_disabled_without_pika_writes(self):
        clock = Clock()
        with tempfile.TemporaryDirectory(prefix="piper-j6-contract-") as temporary, mock.patch.object(comparison, "time", clock):
            fixture = FakeComparison(Path(temporary) / "receipt.json", clock)
            fixture.board["pika"]["enabled"] = False
            fixture.preflight()
            fixture.run()
            self.assertTrue(fixture.receipt["passed"])
            self.assertIs(fixture.receipt["pikaBaselineEnabled"], False)
            self.assertIs(fixture.board["pika"]["enabled"], False)
            self.assertTrue(all(row["state"]["pika"]["enabled"] is False for row in fixture.rows))
            self.assertTrue(all(action.startswith("follow-") for action, _ in fixture.actions))

    def test_changed_gripper_enabled_state_retires_arm_follow_only(self):
        clock = Clock()
        with tempfile.TemporaryDirectory(prefix="piper-j6-contract-") as temporary, mock.patch.object(comparison, "time", clock):
            fixture = FakeComparison(Path(temporary) / "receipt.json", clock, enable_flip=True)
            fixture.board["pika"]["enabled"] = False
            fixture.preflight()
            with self.assertRaisesRegex(RuntimeError, "Pika"):
                fixture.run()
            self.assertEqual(fixture.updates, 5)
            self.assertEqual(fixture.actions[-1][0], "follow-end")
            self.assertFalse(fixture.board["motion"]["active"])

    def test_failed_preflight_saves_redacted_receipt_without_commands(self):
        clock = Clock()
        with tempfile.TemporaryDirectory(prefix="piper-j6-contract-") as temporary, mock.patch.object(comparison, "time", clock):
            fixture = FakeComparison(Path(temporary) / "receipt.json", clock)
            fixture.board["devices"][0]["recovery_required"] = True
            with self.assertRaisesRegex(RuntimeError, "Recovery"):
                fixture.preflight()
            self.assertEqual(fixture.actions, [])
            text = fixture.output.read_text(encoding="utf-8")
            saved = json.loads(text)
            self.assertFalse(saved["preflightPassed"])
            self.assertFalse(saved["executed"])
            self.assertEqual(saved["failureStage"], "preflight")
            self.assertTrue(saved["failureState"]["devices"][0]["recovery_required"])
            self.assertNotIn("secret", text)

    def test_wrong_admission_ack_still_retires_admitted_request(self):
        clock = Clock()
        with tempfile.TemporaryDirectory(prefix="piper-j6-contract-") as temporary, mock.patch.object(comparison, "time", clock):
            fixture = FakeComparison(Path(temporary) / "receipt.json", clock, wrong_ack=True)
            fixture.preflight()
            with self.assertRaisesRegex(RuntimeError, "acknowledge"):
                fixture.run()
            self.assertEqual(fixture.actions[-1][0], "follow-end")
            self.assertFalse(fixture.board["motion"]["active"])
            self.assertFalse(fixture.receipt["passed"])

    def test_http_route_auth_and_receipt_redaction_without_network(self):
        with tempfile.TemporaryDirectory(prefix="piper-j6-contract-") as temporary:
            fixture = comparison.Comparison("http://127.0.0.1:8765", "can-fixture", "fixture-owner-secret", Path(temporary) / "receipt.json")
            fixture.token, fixture.generation = "fixture-token-secret", 7
            def respond(request, timeout):
                self.assertEqual(request.full_url, "http://127.0.0.1:8765/api/command")
                self.assertEqual(request.get_header("X-piper-token"), fixture.token)
                body = json.loads(request.data)
                self.assertEqual(body["owner"], fixture.owner)
                self.assertEqual(body["device_id"], "can-fixture")
                return io.BytesIO(b'{"ok":true,"request_id":123,"trajectory_transport":"streamed-position"}')
            with mock.patch.object(comparison.urllib.request, "urlopen", respond):
                fixture.post("follow-start", dict(trajectory=True, trajectory_transport=comparison.JS))
            self.assertNotIn("secret", json.dumps(fixture.receipt))
            with self.assertRaises(RuntimeError):
                fixture.post("reset")

    def test_lost_admission_ack_holds_only_new_matching_scope(self):
        clock = Clock()
        with tempfile.TemporaryDirectory(prefix="piper-j6-contract-") as temporary, mock.patch.object(comparison, "time", clock):
            fixture = FakeComparison(Path(temporary) / "receipt.json", clock, lost_ack=True)
            fixture.preflight()
            with self.assertRaises(TimeoutError):
                fixture.run()
            self.assertEqual([action for action, _ in fixture.actions], ["follow-start", "follow-end"])
            self.assertFalse(fixture.board["motion"]["active"])
            self.assertFalse(fixture.receipt["passed"])


if __name__ == "__main__":
    unittest.main()

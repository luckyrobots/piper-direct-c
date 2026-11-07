using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class UnifiedWorkspaceTests
{
    public static void Run(Action<bool, string> check)
    {
        JsonObject Device() => new() { ["fresh"] = true, ["recovery_required"] = false,
            ["status"] = new JsonObject { ["control"] = 1, ["teach_state"] = 0, ["arm_status"] = 0, ["error"] = 0 },
            ["joints"] = new JsonArray(Enumerable.Range(0, 6).Select(_ => (JsonNode)new JsonObject { ["fresh"] = true, ["enabled"] = true, ["fault"] = 0, ["actual_deg"] = 0d }).ToArray()) };
        var device = Device();
        foreach (double jitter in new[] { -.03, -.02, -.01, 0, .01, .02, .03 })
        {
            device["joints"]![5]!["actual_deg"] = jitter;
            var ready = RobotReadiness.Inspect(device, true, true, false, false);
            check(ready.Action == "Enabled" && !ready.Enabled && ready.Label.StartsWith("Ready"), "Readout noise changed the enabled-state primary indicator");
        }
        check(RobotReadiness.Inspect(device, false, true, false, false).Action == "Enable robot", "A powered arm exposed a separate Use controls permission action");
        check(RobotReadiness.Inspect(device, true, false, false, false).Action == "Enable robot", "A missing gripper session required a separate enable button");
        foreach (string blocker in new[] { "Firmware has not been read on this connection. Click Read firmware & limits.",
            "Reading joint limits for this connection.", "Reading acceleration limits for this connection." })
        {
            device["blocker"] = blocker;
            check(RobotReadiness.Inspect(device, true, true, false, false) is { Label: "Reading robot settings", Enabled: false } waiting &&
                waiting.Action != "Enabled" && waiting.Detail.Contains(blocker, StringComparison.Ordinal),
                "A fully connected arm with unread configuration was falsely marked Ready or could start following");
            check(RobotReadiness.Inspect(device, false, true, false, false) is { Action: "Enable robot", Enabled: true },
                "A pending read-only firmware/limits query hid initial Enable and created a dead end");
            check(RobotReadiness.Inspect(device, true, false, false, false) is { Action: "Enable robot", Enabled: true },
                "A pending controller query hid the action for connecting the assigned gripper");
            check(RobotReadiness.Inspect(device, false, false, true, false) is { Action: "Stop following", Enabled: true },
                "Unread controller configuration hid the active follower's immediate stop action");
        }
        foreach (string blocker in new[] { "Another controller is sending commands on this CAN bus.",
            "A motor driver reports a fault.", "The reported hardware/firmware is not supported for motion." })
        {
            device["blocker"] = blocker;
            check(RobotReadiness.Inspect(device, true, true, false, false) is { Label: "Controller needs attention", Enabled: false } blocked &&
                blocked.Detail == blocker, "A native safety blocker was hidden behind Ready");
            check(!RobotReadiness.Inspect(device, false, false, false, false).Enabled,
                "Initial connection bypassed a native fault/bus/firmware blocker");
            check(RobotReadiness.Inspect(device, false, false, true, false) is { Action: "Stop following", Enabled: true },
                "A native blocker hid the active follower stop action");
        }
        device["blocker"] = "Control action in progress.";
        check(RobotReadiness.Inspect(device, true, true, false, true) is { Label: "Movement in progress", Enabled: false },
            "An ordinary active move was mislabeled as a controller fault");
        device["blocker"] = "";
        foreach (double strictDwell in new[] { 0d, .01, .2, 2d })
        {
            device["status"]!["stationary_s"] = strictDwell;
            check(RobotReadiness.Inspect(device, true, true, false, false) is { Action: "Enabled", Enabled: false },
                "Strict raw-speed dwell fluctuations changed a healthy stable-pose primary action");
        }
        device["recovery_required"] = true; device["status"]!["control"] = 2;
        check(RobotReadiness.Inspect(device, false, true, false, false) is { Action: "Recover robot", Enabled: true, Label: "Stopped" }, "Completed teaching hid the recovery action behind disabled Return to CAN");
        device["status"]!["teach_state"] = 1;
        check(RobotReadiness.Inspect(device, false, true, false, false).Label == "Hand guiding", "A software stop hid the physical hand-guiding mode");
        device["status"]!["control"] = 0; device["status"]!["arm_status"] = 1;
        foreach (int residual in new[] { 1, 3, 4, 5, 7 })
        {
            device["status"]!["teach_state"] = residual;
            device["teaching"] = new JsonObject { ["phase"] = residual == 1 ? "hand-guiding" : "playback", ["active"] = true, ["return_required"] = true };
            var stoppedTeaching = TeachingState.Inspect(device);
            check(stoppedTeaching is { Phase: "stopped", Active: false, ReturnRequired: false, CanReturn: false, BlocksTargets: true } &&
                stoppedTeaching.NextStep.Contains("reset", StringComparison.OrdinalIgnoreCase),
                "Latched STOP was presented as healthy hand guiding/playback from its residual teach byte");
            check(RobotReadiness.Inspect(device, false, true, false, false) is { Label: "Stopped", Action: "Recover robot", Enabled: true } stopped &&
                stopped.Detail.Contains("reset", StringComparison.OrdinalIgnoreCase),
                "Residual teaching hid the recorded STOP recovery action");
        }
        check(RobotReadiness.Inspect(null, false, false, true, false) is { Action: "Stop following", Enabled: true }, "Feedback loss hid the active follower's immediate stop action");

        var first = new RobotProfile("one", "One", PikaDevice: "/dev/pika/by-host/11-4");
        var second = new RobotProfile("two", "Two", PikaDevice: "/dev/pika/by-host/15-4");
        var snapshot = new JsonObject { ["pikas"] = new JsonArray(
            new JsonObject { ["device"] = first.PikaDevice, ["distribution"] = "Ubuntu", ["position"] = 1d },
            new JsonObject { ["device"] = second.PikaDevice, ["distribution"] = "Ubuntu", ["position"] = 2d }) };
        check(ProfilePikaRouting.Find(snapshot, first)?["position"]?.GetValue<double>() == 1 && ProfilePikaRouting.Find(snapshot, second)?["position"]?.GetValue<double>() == 2,
            "Selecting a second gripper replaced or reused the first device's telemetry");
        check(ProfilePikaRouting.Find(snapshot, first with { PikaDevice = "/dev/missing" }) is null, "A missing Pika assignment silently fell back to a different gripper");
        string gripperOwner = Guid.NewGuid().ToString();
        var disabledGripper = new JsonObject { ["owner"] = gripperOwner, ["device"] = first.PikaDevice,
            ["configured"] = true, ["connected"] = true, ["fresh"] = true, ["enabled"] = false, ["armed"] = false,
            ["pending"] = false, ["following"] = false, ["resuming_hold"] = false, ["power_uncertain"] = false,
            ["power_requested"] = true, ["last_motor_request"] = "disable-proof", ["age_s"] = .01,
            ["request_time_s"] = 10d, ["feedback_time_s"] = 10.1 };
        check(ProfilePikaRouting.CanReleaseAssignment(disabledGripper, gripperOwner, true),
            "A causally verified disable kept the gripper assignment locked by historical power_requested");
        check(!ProfilePikaRouting.CanReleaseAssignment(disabledGripper, gripperOwner, false) &&
            !ProfilePikaRouting.CanReleaseAssignment(disabledGripper, Guid.NewGuid().ToString(), true),
            "Stale or another owner's disable proof released a powered gripper assignment");
        disabledGripper["feedback_time_s"] = 9d;
        check(!ProfilePikaRouting.CanReleaseAssignment(disabledGripper, gripperOwner, true),
            "Feedback preceding disable released a gripper assignment");
        disabledGripper["feedback_time_s"] = 10.1;
        disabledGripper["enabled"] = true;
        check(!ProfilePikaRouting.CanReleaseAssignment(disabledGripper, gripperOwner, true),
            "An enabled gripper could be reassigned while holding");
        disabledGripper["enabled"] = false; disabledGripper["power_requested"] = false;
        disabledGripper["last_motor_request"] = "";
        check(ProfilePikaRouting.CanReleaseAssignment(disabledGripper, gripperOwner, true),
            "A fresh read-only gripper session required a nonexistent motor request before reassignment");
        var guiding = new TeachingState("hand-guiding", true, true, false, "", "") { RequiresZero = false };
        var ended = new TeachingState("ended", false, true, false, "", "") { RequiresZero = false };
        var gate = new HandGuideRecoveryGate("identity-a");
        check(!gate.Observe("identity-a", ended, true), "Loading a completed state granted recovery without an observed guide cycle");
        check(!gate.Observe("identity-b", guiding, true) && !gate.SawGuiding, "A different arm supplied the physical guide proof");
        check(!gate.Observe("identity-a", guiding, true) && gate.SawGuiding, "Hand guiding dispatched recovery before the light turned off");
        check(!gate.Observe("identity-a", ended, false), "UI recovery bypassed native qualification");
        check(gate.Observe("identity-a", ended, true) && !gate.Observe("identity-a", ended, true), "A physical guide cycle did not authorize exactly one qualified return");
        var nativeObserved = new HandGuideRecoveryGate("identity-a", observedGuiding: true);
        check(nativeObserved.Observe("identity-a", ended, true), "Explicit recovery discarded a fresh native-observed physical guide cycle");
    }
}

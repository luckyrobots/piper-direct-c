using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class TeachingResetTests
{
    public static async Task Run(Action<bool, string> check)
    {
        JsonObject Device() => new()
        {
            ["id"] = "reset-arm", ["generation"] = 7L, ["fresh"] = true, ["firmware_supported"] = true,
            ["reset_pending"] = false, ["recovery_required"] = false,
            ["status"] = new JsonObject { ["control"] = 2, ["teach_state"] = 2, ["motion_mode"] = 1,
                ["arm_status"] = 0, ["error"] = 0, ["stationary_s"] = 1.0 },
            ["joints"] = new JsonArray(new[] { 3.7, -2.3, -.1, -7.6, 30.9, -.85 }.Select(angle => (JsonNode)new JsonObject
                { ["actual_deg"] = angle, ["fresh"] = true, ["fault"] = 0, ["enabled"] = true }).ToArray())
        };
        bool Eligible(JsonObject device, bool moving = false) => TeachingResetAssisted.CanResetAssisted(new(device, moving));
        void Rejected(Action<JsonObject> mutate, string label)
        {
            var device = Device(); mutate(device);
            check(!Eligible(device), "Reset-assisted posture/health guard accepted " + label);
        }
        check(Eligible(Device()), "Supported nonzero teaching-return posture was rejected");
        foreach (int joint in new[] { 1, 2 })
        {
            foreach (double boundary in new[] { -10.0, 10.0 })
                Rejected(d => d["joints"]![joint]!["actual_deg"] = boundary, $"J{joint + 1} boundary {boundary}");
            foreach (double inside in new[] { -9.999, 9.999 })
            {
                var device = Device(); device["joints"]![joint]!["actual_deg"] = inside;
                check(Eligible(device), $"J{joint + 1} strict reset-posture interior {inside} was rejected");
            }
        }
        foreach (double boundary in new[] { 12.0, 45.0 })
            Rejected(d => d["joints"]![4]!["actual_deg"] = boundary, $"J5 boundary {boundary}");
        foreach (double inside in new[] { 12.001, 44.999 })
        {
            var device = Device(); device["joints"]![4]!["actual_deg"] = inside;
            check(Eligible(device), "J5 reset-posture interior was rejected");
        }
        Rejected(d => d["fresh"] = false, "stale controller feedback");
        Rejected(d => d["firmware_supported"] = false, "unsupported firmware");
        Rejected(d => d["joints"]![3]!["fresh"] = false, "stale joint feedback");
        Rejected(d => d["joints"]![3]!["fault"] = 1, "a driver fault");
        Rejected(d => d["joints"]![3]!["actual_deg"] = double.NaN, "a nonfinite joint measurement");
        Rejected(d => d["joints"]!.AsArray().RemoveAt(5), "an incomplete joint set");
        Rejected(d => d["status"]!["error"] = 1, "a controller fault");
        Rejected(d => d["status"]!["arm_status"] = 5, "a communication error");
        Rejected(d => d["status"]!["teach_state"] = 1, "active hand guiding");
        Rejected(d => d["status"]!["teach_state"] = 3, "onboard playback");
        Rejected(d => d["status"]!["control"] = 1, "ordinary CAN mode as ended teaching");
        Rejected(d => d["status"]!["motion_mode"] = 3, "an unsupported motion mode");
        Rejected(d => d["reset_pending"] = true, "a pending reset");
        Rejected(d => d["status"]!["stationary_s"] = .399, "insufficient stationary time");
        Rejected(d => d["status"]!["stationary_s"] = double.NaN, "nonfinite stationary time");
        var settled = Device(); settled["status"]!["stationary_s"] = .4;
        check(Eligible(settled), "Exactly 0.4 seconds of stationary feedback was rejected");
        check(!Eligible(Device(), moving: true), "An active motion lease entered reset-assisted return");

        async Task Failure(Func<Task> operation, string label)
        {
            Exception? failure = null;
            try { await operation(); } catch (Exception error) { failure = error; }
            check(failure is not null, label);
        }
        void SafeActions(List<string> actions) => check(actions.All(a => a is "stop" or "reset" or "verify-current" or "verify-reset" or "enable"),
            "Reset-assisted return emitted a trajectory, joint target or unsupported action");
        async Task Execute(JsonObject device, List<string> actions, Action<string>? alter = null, bool pendingReset = false,
            CancellationToken cancellation = default, Func<TeachingResetSnapshot>? snapshot = null, bool confirmed = true,
            string deviceId = "reset-arm", long generation = 7, int timeout = 100)
        {
            await TeachingResetAssisted.Run(confirmed, deviceId, generation, snapshot ?? (() => new(device, false)),
                (action, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    actions.Add(action);
                    switch (action)
                    {
                        case "stop": device["status"]!["arm_status"] = 1; break;
                        case "reset":
                            device["status"]!["control"] = 0; device["status"]!["teach_state"] = 0;
                            device["status"]!["arm_status"] = 0;
                            device["reset_pending"] = pendingReset;
                            device["recovery_required"] = pendingReset;
                            foreach (var joint in device["joints"]!.AsArray()) joint!["enabled"] = false;
                            break;
                        case "verify-current" or "verify-reset":
                            device["reset_pending"] = false; device["recovery_required"] = false; break;
                        case "enable":
                            foreach (var joint in device["joints"]!.AsArray()) joint!["enabled"] = true;
                            break;
                        default: throw new InvalidOperationException("Unexpected hardware action in fake reset pipeline.");
                    }
                    alter?.Invoke(action);
                    return Task.CompletedTask;
                }, _ => { }, cancellation, timeout);
        }

        foreach (bool pendingReset in new[] { false, true })
        {
            var device = Device(); var actions = new List<string>();
            bool verifiedOff = false, enabledAfterVerification = false;
            await Execute(device, actions, action =>
            {
                if (action is "verify-current" or "verify-reset")
                    verifiedOff = device["joints"]!.AsArray().All(j => j!["enabled"]!.GetValue<bool>() == false);
                if (action == "enable") enabledAfterVerification = verifiedOff;
            }, pendingReset);
            check(actions.SequenceEqual(new[] { "stop", "reset", pendingReset ? "verify-reset" : "verify-current", "enable" }),
                "Reset-assisted sequence did not follow STOP → reset → verified-off → explicit enable");
            check(verifiedOff && enabledAfterVerification, "Six-motor disable verification was skipped before enabling");
            check(device["joints"]!.AsArray().All(j => j!["enabled"]!.GetValue<bool>()) && !device["recovery_required"]!.GetValue<bool>(),
                "Successful fake reset return did not finish with six verified enabled motors");
            SafeActions(actions);
        }

        {
            var actions = new List<string>();
            await Failure(() => Execute(Device(), actions, confirmed: false), "Missing support/power-loss consent started the reset pipeline");
            check(actions.Count == 0, "An unconfirmed reset-assisted return sent a command");
        }
        foreach (bool mismatchGeneration in new[] { false, true })
        {
            var actions = new List<string>();
            await Failure(() => Execute(Device(), actions, deviceId: mismatchGeneration ? "reset-arm" : "another-arm", generation: mismatchGeneration ? 8 : 7),
                "An initial device/generation mismatch entered reset-assisted return");
            check(actions.Count == 0, "Initial identity mismatch caused a command on the wrong connection");
        }
        {
            var device = Device(); var actions = new List<string>();
            await Failure(() => Execute(device, actions, action => { if (action == "stop") device["generation"] = 8L; }),
                "A reconnect after STOP did not cancel reset-assisted return");
            check(actions.SequenceEqual(new[] { "stop" }), "A changed connection received reset, enable or cleanup STOP");
        }
        {
            var device = Device(); var actions = new List<string>();
            await Failure(() => Execute(device, actions, action => { if (action == "stop") device["joints"]![4]!["actual_deg"] = 45.0; }),
                "Posture changed after STOP but reset still proceeded");
            check(actions.SequenceEqual(new[] { "stop", "stop" }), "Post-STOP posture failure did not stop without resetting/enabling");
        }
        {
            var device = Device(); var actions = new List<string>();
            await Failure(() => Execute(device, actions, action => { if (action == "stop") device["joints"]![0]!["fault"] = 1; }),
                "A driver fault after STOP did not cancel the reset flow");
            check(actions.SequenceEqual(new[] { "stop", "stop" }), "Post-STOP driver fault progressed into reset/enable");
        }
        foreach (string failingAction in new[] { "reset", "verify-current", "enable" })
        {
            var device = Device(); var actions = new List<string>();
            await Failure(() => Execute(device, actions, action => { if (action == failingAction) throw new IOException("Injected command failure"); }),
                "A failed command was hidden by the reset workflow");
            check(actions[^1] == "stop" && actions.Count(a => a == failingAction) == 1, "A failed stage retried or omitted cleanup STOP");
            if (failingAction != "enable") check(!actions.Contains("enable"), "Failure before verified enable still powered motors");
            SafeActions(actions);
        }
        {
            var device = Device(); var actions = new List<string>();
            await Failure(() => Execute(device, actions, action =>
            {
                if (action == "reset") foreach (var joint in device["joints"]!.AsArray()) joint!["enabled"] = true;
            }, timeout: 30), "Missing disabled-motor acknowledgement did not time out");
            check(actions.SequenceEqual(new[] { "stop", "reset", "stop" }), "Unverified reset powered motors or skipped cleanup STOP");
        }
        {
            var device = Device(); var actions = new List<string>();
            await Failure(() => Execute(device, actions, action => { if (action == "reset") device["fresh"] = false; }, timeout: 30),
                "Persistently stale reset feedback did not abort");
            check(actions.SequenceEqual(new[] { "stop", "reset", "stop" }), "Stale reset feedback allowed verify/enable or skipped STOP");
        }
        {
            var device = Device(); var actions = new List<string>(); int staleReads = 0; bool resetting = false;
            await Execute(device, actions, action => { if (action == "reset") { resetting = true; device["fresh"] = false; } },
                snapshot: () =>
                {
                    if (resetting && ++staleReads >= 3) device["fresh"] = true;
                    return new(device, false);
                });
            check(staleReads >= 3 && actions.SequenceEqual(new[] { "stop", "reset", "verify-current", "enable" }),
                "A transient reset feedback gap was treated as success without waiting, or aborted before fresh motor-off verification");
        }
        {
            var device = Device(); var actions = new List<string>(); int verificationReads = 0; bool verifying = false;
            await Execute(device, actions, action =>
            {
                if (action == "verify-reset")
                {
                    verifying = true; device["reset_pending"] = true; device["recovery_required"] = true;
                }
            }, pendingReset: true, snapshot: () =>
            {
                if (verifying && ++verificationReads >= 3)
                {
                    device["reset_pending"] = false; device["recovery_required"] = false;
                }
                return new(device, false);
            });
            check(verificationReads >= 3 && actions.SequenceEqual(new[] { "stop", "reset", "verify-reset", "enable" }),
                "Enable ignored a delayed fresh reset-verification acknowledgement");
        }
        {
            var device = Device(); var actions = new List<string>();
            using var cancel = new CancellationTokenSource();
            await Failure(() => Execute(device, actions, action => { if (action == "stop") cancel.Cancel(); }, cancellation: cancel.Token),
                "Cancellation after STOP did not stop reset-assisted return");
            check(actions.SequenceEqual(new[] { "stop", "stop" }), "Cancelled reset workflow reset/enabled or failed to issue bounded cleanup STOP");
        }
        {
            var device = Device(); var actions = new List<string>();
            await Failure(() => Execute(device, actions, action => { if (action == "verify-current") device["generation"] = 8L; }),
                "Connection changed after verification but enabling continued");
            check(actions.SequenceEqual(new[] { "stop", "reset", "verify-current" }), "Reconnect after verification received enable or cleanup commands");
        }
    }
}

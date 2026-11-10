using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class TeachingResumeTests
{
    public static void Run()
    {
        JsonObject Device() => new()
        {
            ["fresh"] = true, ["recovery_required"] = false, ["reset_pending"] = false,
            ["status"] = new JsonObject { ["control"] = 2, ["teach_state"] = 2, ["arm_status"] = 0, ["error"] = 0 },
            ["teaching"] = new JsonObject { ["phase"] = "ended", ["return_required"] = true, ["can_return"] = false,
                ["return_reason"] = "Hold the arm still until all six motor encoders confirm stationary feedback for 0.4 seconds.",
                ["seamless_supported"] = true, ["return_requires_zero"] = false, ["return_strategy"] = "measured-pose" },
            ["joints"] = new JsonArray(Enumerable.Range(0, 6).Select(_ => (JsonNode)new JsonObject { ["fresh"] = true, ["enabled"] = true, ["fault"] = 0 }).ToArray())
        };
        var device = Device();
        string? text = null;
        foreach (bool eligible in new[] { false, true, false, false, true })
        {
            device["teaching"]!["can_return"] = eligible;
            var readiness = RobotReadiness.Inspect(device, false, false, false, false);
            Check(readiness is { Action: "Resume controls", Enabled: true, Label: "Hand guiding off" },
                "A transient stillness gate changed the primary Resume click target");
            var state = TeachingState.Inspect(device);
            text ??= state.NextStep;
            Check(state.NextStep == text && !state.NextStep.Contains("Turn the teach light off", StringComparison.Ordinal),
                "Completed hand guiding alternated its instructions with raw stillness telemetry");
            Check(TeachingResumeCheck.Ready(device) == eligible, "Resume eligibility bypassed or hid native stillness admission");
        }
        foreach (Action<JsonObject> fault in new Action<JsonObject>[]
        {
            d => d["fresh"] = false, d => d["recovery_required"] = true, d => d["reset_pending"] = true,
            d => d["status"]!["arm_status"] = 1, d => d["status"]!["error"] = 1,
            d => d["joints"]![3]!["fault"] = 1, d => d["joints"]![4]!["enabled"] = false,
            d => d["joints"]![5]!["fresh"] = false,
            d => d["teaching"]!["phase"] = "hand-guiding", d => d["teaching"]!["phase"] = "playback",
            d => d["teaching"]!["return_reason"] = "A joint requires more than 3 degrees of range correction; inspect the physical pose before teaching handoff."
        })
        {
            var failed = Device(); fault(failed);
            bool rejected = false;
            try { TeachingResumeCheck.Ready(failed); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "A stale, faulted, unpowered, active teaching or range-blocked resume was treated as a stillness wait");
        }
        foreach (bool failInsideGrant in new[] { false, true })
        {
            int epoch = 1; bool approved = false, remembered = false, rolledBack = false;
            bool rejected = false;
            try
            {
                TeachingResumeCheck.CommitApproval(() => { if (epoch != 1) throw new InvalidOperationException("revoked"); }, () =>
                {
                    approved = remembered = true; epoch = 2;
                    if (failInsideGrant) throw new InvalidOperationException("feedback changed during grant");
                }, () => { approved = remembered = false; rolledBack = true; });
            }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && rolledBack && !approved && !remembered,
                "Concurrent permission revocation or grant failure resurrected resume approval");
        }
    }
}

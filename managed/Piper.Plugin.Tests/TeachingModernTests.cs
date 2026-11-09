using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class TeachingModernTests
{
    public static void Run(Action<bool, string> check)
    {
        JsonObject Device(string strategy = "measured-pose") => new()
        {
            ["fresh"] = true, ["status"] = new JsonObject { ["control"] = 2, ["teach_state"] = 2 },
            ["teaching"] = new JsonObject { ["phase"] = "ended", ["return_required"] = true, ["can_return"] = true,
                ["seamless_supported"] = true, ["return_requires_zero"] = false, ["return_strategy"] = strategy }
        };
        TeachingState modern = TeachingState.Inspect(Device());
        check(modern.SamePoseReturn && !modern.RequiresZero && modern.CanReturn, "Modern backend capability did not expose measured-pose return");
        check(modern.ReturnFlags()["same_pose_confirmed"]?.GetValue<bool>() == true && modern.ReturnFlags()["zero_pose_confirmed"] is null,
            "Modern return mislabeled its request as physical-zero confirmation");
        check(modern.NextStep.Contains("no need to move to zero") && modern.ReturnConfirmation.Contains("measured pose"), "Modern return still demanded zero");
        var missing = Device(); missing.Remove("teaching");
        check(TeachingState.Inspect(missing).RequiresZero, "Missing backend capability bypassed the legacy guard");
        check(TeachingState.Inspect(Device("future-unknown")).RequiresZero, "Unknown handoff strategy bypassed the legacy guard");
        missing = Device(); missing["teaching"]!["seamless_supported"] = false;
        check(TeachingState.Inspect(missing).RequiresZero, "Unqualified firmware enabled same-pose return");
        var legacy = TeachingState.Inspect(missing);
        check(legacy.ReturnFlags()["zero_pose_confirmed"]?.GetValue<bool>() == true && legacy.ReturnFlags()["same_pose_confirmed"] is null,
            "Legacy return changed its zero-only API contract");

        TeachingState guiding = modern with { Phase = TeachingPhase.HandGuiding, Active = true, CanReturn = false };
        var gate = new TeachingReturnGate();
        check(!gate.Observe(modern, true, true, "connection-a"), "Loading an ended controller state issued an automatic return");
        check(!gate.Observe(guiding, true, true, "connection-a"), "Entering hand guiding changed controller mode");
        check(!gate.Observe(modern with { CanReturn = false }, true, true, "connection-a"), "Unready handoff triggered a return");
        check(gate.Observe(modern, true, true, "connection-a"), "Approved local hand-guiding session did not return when healthy");
        check(!gate.Observe(modern, true, true, "connection-a"), "Failed or accepted return was retried without another session");
        gate.Observe(guiding, false, true, "connection-a");
        check(!gate.Observe(modern, false, true, "connection-a"), "An unapproved hand-guiding session granted CAN authority");
        gate.Observe(guiding, true, true, "connection-a");
        check(!gate.Observe(modern, true, true, "connection-b"), "Backend identity/generation change retained automatic authority");
        gate.Observe(guiding, true, true, "connection-b");
        check(!gate.Observe(legacy, true, true, "connection-b"), "Legacy firmware returned automatically without its explicit zero flow");
        gate.Observe(guiding, true, true, "connection-b");
        gate.Observe(modern with { Phase = TeachingPhase.Playback, Active = true }, true, true, "connection-b");
        check(!gate.Observe(modern, true, true, "connection-b"), "Onboard playback completion was mistaken for approved hand guiding");
        gate.Observe(guiding, true, true, "connection-b");
        gate.Observe(modern, true, false, "connection-b");
        check(!gate.Observe(modern, true, true, "connection-b"), "Leaving Controls preserved an automatic mode-change intent");
        gate.Observe(guiding, true, true, "connection-b");
        gate.Observe(TeachingState.Inspect(null), true, true, "connection-b");
        check(!gate.Observe(modern, true, true, "connection-b"), "A lost status stream retained automatic mode-change authority");
        gate.Observe(guiding, true, true, "connection-b");
        gate.Invalidate(); // Reader-side disconnect may occur between rendered polls.
        check(!gate.Observe(modern, true, true, "connection-b"), "A stream reconnect between UI polls retained automatic return authority");
    }
}

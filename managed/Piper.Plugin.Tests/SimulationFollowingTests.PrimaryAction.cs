using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Plugin;

internal static partial class SimulationFollowingTests
{
    internal static async Task RunPrimaryWorkspaceAction()
    {
        foreach (bool missingLink in new[] { false, true })
        {
            await using var f = new Fixture();
            if (missingLink) f.RemoveMeasuredLink(editorDelayMilliseconds: 250);
            f.UseUiDispatcher();
            await f.Navigate("settings.robot");
            await f.Navigate("controls");
            await f.Click("enable");
            _ = await WaitPrimary(f, "complete");
            Check(f.Plugin.link is not null && f.Plugin.approved && f.PrimaryActionName() == "Enabled",
                "Primary Enable did not leave a ready linked robot after Settings navigation");
            Check(f.Session is null && !f.Ready && !f.PlayStarted && f.Mode == "edit" && !f.EngineRequests.Any(r => r.Method == "robot.runtime.control"),
                "Primary Enable silently prepared a runtime or entered Play instead of waiting for the global group action");
        }
        await using (var blocked = new Fixture())
        {
            blocked.SetSpeed(0); blocked.UseUiDispatcher();
            await blocked.Click("enable");
            var receipt = await WaitPrimary(blocked, "complete");
            Check(receipt["stage"]?.GetValue<string>() == "complete" && receipt["failure"] is null &&
                !receipt.ToJsonString().Contains("fixture-backend", StringComparison.Ordinal),
                "Enable at zero selected speed did not complete its control-only lifecycle or leaked the backend token");
            Check(blocked.Session is null && !blocked.EngineRequests.Any(r => r.Action == "prepare-start") && blocked.Rpc.Requests.IsEmpty,
                "A blocked primary action prepared a runtime or sent a physical request");
        }
        await using (var gripperUnready = new Fixture())
        {
            gripperUnready.SetGripperUnready(); gripperUnready.UseUiDispatcher();
            await gripperUnready.Navigate("settings.robot"); await gripperUnready.Navigate("controls");
            Check(gripperUnready.Plugin.approved && RobotPowerState.Inspect(gripperUnready.Snapshot["devices"]![0]!.AsObject()).AllEnabled &&
                gripperUnready.PrimaryActionName() == "Enable robot", "Unready-gripper fixture did not exercise the approved, powered arm's Enable label");
            await gripperUnready.Click("enable");
            _ = await WaitPrimary(gripperUnready, "complete");
            Check(gripperUnready.Rpc.Requests.Select(r => r.Route).SequenceEqual(["/api/pika/connect", "/api/pika/enable"]) &&
                gripperUnready.Plugin.PikaRole == PikaSessionRole.Owned && gripperUnready.PrimaryActionName() == "Enabled",
                "Enable robot did not finish the assigned read-only/unready gripper connection");
            Check(gripperUnready.Session is null && !gripperUnready.Ready && !gripperUnready.PlayStarted && gripperUnready.Mode == "edit" &&
                !gripperUnready.EngineRequests.Any(r => r.Method == "robot.runtime.control"),
                "An Enable robot click silently toggled simulation following or started first-pose matching after gripper enable");
        }
        foreach (bool failConfirmation in new[] { false, true })
        {
            await using var confirmed = new Fixture();
            confirmed.SetApproved(false); confirmed.UseUiDispatcher();
            await confirmed.Click("enable");
            var initial = await WaitPrimary(confirmed, "complete");
            Check(!confirmed.Plugin.approved && confirmed.Session is null && confirmed.Rpc.Requests.IsEmpty,
                "An unconfirmed Enable click acquired robot control or prepared following");
            if (failConfirmation)
            {
                confirmed.Snapshot["devices"]![0]!["status"]!["control"] = 2;
                confirmed.Snapshot["devices"]![0]!["status"]!["teach_state"] = 1;
            }
            await confirmed.Click("safety.accept");
            var result = await WaitPrimary(confirmed, failConfirmation ? "failed" : "complete", initial["id"]!.GetValue<string>());
            Check(confirmed.Plugin.primaryAction.Phase == PrimaryActionPhase.Idle && (failConfirmation
                ? !confirmed.Plugin.approved && confirmed.Plugin.primaryAction.Status.StartsWith("Could not start:", StringComparison.Ordinal) && result["failure"]!.GetValue<string>().Contains("hand guiding", StringComparison.OrdinalIgnoreCase)
                : confirmed.Plugin.primaryAction.Status.Length == 0 && result["failure"] is null && confirmed.Plugin.message.StartsWith("Ready ·", StringComparison.Ordinal)),
                "Safety confirmation did not complete its shared progress/failure lifecycle");
            Check(confirmed.Session is null && !confirmed.EngineRequests.Any(r => r.Action == "prepare-start") && confirmed.Rpc.Requests.IsEmpty,
                "Safety confirmation of an already enabled arm silently started following or sent a physical request");
        }

        static async Task<JsonObject> WaitPrimary(Fixture fixture, string stage, string? previousId = null)
        {
            long began = Stopwatch.GetTimestamp();
            while (true)
            {
                if (fixture.Plugin.primaryAction.Phase == PrimaryActionPhase.Idle && File.Exists(fixture.PrimaryReceipt))
                {
                    var receipt = JsonNode.Parse(await File.ReadAllTextAsync(fixture.PrimaryReceipt))!.AsObject();
                    if (receipt["stage"]?.GetValue<string>() == stage && receipt["id"]?.GetValue<string>() != previousId) return receipt;
                }
                if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5)
                    throw new InvalidOperationException("Primary confirmation did not finish as " + stage + ": " + fixture.Plugin.message);
                await Task.Delay(10);
            }
        }
    }
}

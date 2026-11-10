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
    internal static async Task RunTeachingResume()
    {
        await using (var f = new Fixture())
        {
            f.ConfigureTeachingResume();
            Task resume = f.PrimaryResume();
            for (int i = 0; i < 100 && f.Plugin.workspace.PrimaryAction.Current.Phase != PrimaryActionPhase.ResumingControls; i++) { f.Refresh(); await Task.Delay(5); }
            string pending = f.Plugin.workspace.PrimaryAction.Current.Status;
            for (int i = 0; i < 5; i++) { f.Refresh(); await Task.Delay(40); }
            Check(f.Plugin.workspace.PrimaryAction.Current.Phase == PrimaryActionPhase.ResumingControls && f.Rpc.Requests.IsEmpty &&
                f.Plugin.workspace.PrimaryAction.Current.Status == pending && pending.Contains("Hold the arm still", StringComparison.Ordinal),
                "A pending Resume flickered, exited early or issued a command before native stillness admission");
            var patch = f.PrimaryResumePatch();
            Check(patch["text"]?.GetValue<string>() == "Resuming controls…" && patch["enabled"]?.GetValue<bool>() == false,
                "Final unified patches overrode the stable pending Resume button");
            f.SetResumeEligible();
            await resume.WaitAsync(TimeSpan.FromSeconds(2));
            var request = f.Rpc.Requests.Single();
            Check(request.Route == "/api/command" && request.Body["action"]?.GetValue<string>() == "teach-return" &&
                request.Body["device_id"]?.GetValue<string>() == "can-a" && request.Body["generation"]?.GetValue<long>() == 7 &&
                request.Body["same_pose_confirmed"]?.GetValue<bool>() == true,
                "One explicit resume did not send exactly one pinned measured-pose handoff");
            Check(f.Plugin.approved && f.Plugin.workspace.PrimaryAction.Current.Phase == PrimaryActionPhase.Idle &&
                f.Plugin.workspace.PrimaryAction.Current.Status.StartsWith("Controls restored", StringComparison.Ordinal) && f.Mode == "edit",
                "Successful resume failed its final approval/presentation or entered Play");
        }
        await using (var f = new Fixture())
        {
            f.ConfigureTeachingResume(completeImmediately: false); f.SetResumeEligible();
            f.Rpc.DelayedOperation = "heartbeat";
            Task resume = f.PrimaryResume();
            await f.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!resume.IsCompleted && !f.Plugin.approved && f.Plugin.workspace.PrimaryAction.Current.Phase == PrimaryActionPhase.ResumingControls,
                "CAN mode alone completed Resume before the native handoff acknowledgement");
            var pendingMotion = f.Plugin.telemetry.Latest!.State["devices"]![0]!["motion"]!;
            f.CompleteResume();
            f.Rpc.ReleaseDelayed.TrySetResult();
            Check(pendingMotion["active"]?.GetValue<bool>() == true && pendingMotion["phase"]?.GetValue<string>() == "teaching-return",
                "Publishing the completion mutated an earlier active telemetry frame");
            await resume.WaitAsync(TimeSpan.FromSeconds(2));
            Check(f.Rpc.Requests.Count(r => r.Route == "/api/command") == 1 &&
                f.Rpc.Requests.Where(r => r.Route == "/api/heartbeat").All(r => r.Body["device_id"]?.GetValue<string>() == "can-a" &&
                    r.Body["generation"]?.GetValue<long>() == 7 && r.Body["request_id"]?.GetValue<long>() == 77),
                "Resume retried admission or sent an unscoped handoff heartbeat");
        }
        foreach (string interruption in new[] { "permission", "generation", "playback", "fault" })
        {
            await using var f = new Fixture(); f.ConfigureTeachingResume();
            Task resume = f.PrimaryResume();
            for (int i = 0; i < 100 && f.Plugin.workspace.PrimaryAction.Current.Phase != PrimaryActionPhase.ResumingControls; i++) { f.Refresh(); await Task.Delay(5); }
            f.InterruptResume(interruption);
            bool rejected = false;
            try { await resume.WaitAsync(TimeSpan.FromSeconds(2)); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && f.Rpc.Requests.IsEmpty && !f.Plugin.approved &&
                f.Plugin.workspace.PrimaryAction.Current.Phase == PrimaryActionPhase.Idle && f.Plugin.workspace.PrimaryAction.Current.Status.StartsWith("Could not start:", StringComparison.Ordinal),
                "A pending resume survived revocation, identity change, playback or a fault, or hid its failure");
        }
        await using (var f = new Fixture())
        {
            f.ConfigureTeachingResume(completeImmediately: false); f.SetResumeEligible(); f.CompleteResumeOnHeartbeat();
            await f.PrimaryResume().WaitAsync(TimeSpan.FromSeconds(2));
            Check(f.Plugin.approved && f.Rpc.Requests.Count(r => r.Route == "/api/command") == 1 &&
                f.Rpc.Requests.Count(r => r.Route == "/api/heartbeat") == 1,
                "A final heartbeat racing native completion falsely failed or retried a successful resume");
        }
    }
}

using System.Diagnostics;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;
using LuckyRobots.Piper.Pika;

internal static partial class SimulationFollowingTests
{
    internal static async Task RunUnifiedFollowing(Action<bool, string> check)
    {
        static async Task Until(Func<bool> condition, string reason)
        {
            long began = Stopwatch.GetTimestamp();
            while (!condition())
            { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5) throw new InvalidOperationException(reason); await Task.Delay(10); }
        }
        foreach (string selected in new[] { "piper-1", "piper-2" })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.SelectGroupFollowers(selected); f.UseUiDispatcher();
            await f.Click("group.follow.toggle");
            await Until(() => f.GroupArmedForPlay, "A single selected participant did not arm in Edit");
            check(f.Mode == "edit" && f.Rpc.Requests.IsEmpty && f.EngineRequests.Count(r => r.Action == "arm-next-play") == 1,
                "Single-participant arming entered Play or sent a target before user Play");
            f.RequestGroupPlay();
            await Until(() => f.GroupStage == "following" || !f.GroupActive, "One grouped participant did not start");
            check(f.GroupActive && f.Mode == "play", "The shared executor rejected a single selected participant: " + f.Get<string>("GroupFollowStatus"));
            string can = selected == "piper-1" ? "can-a" : "can-b", pika = selected == "piper-1" ? "/dev/pika-a" : "/dev/pika-b";
            check(f.Rpc.Requests.Where(r => r.Body["action"]?.GetValue<string>() == "follow-start").All(r => r.Body["device_id"]?.GetValue<string>() == can) &&
                f.Rpc.Requests.Where(r => r.Route == "/api/pika/follow-start").All(r => r.Body["pika_device"]?.GetValue<string>() == pika),
                "One participant's targets escaped to an unchecked arm or gripper");
            var mapping = f.EngineRequests.Single(r => r.Method == "robot.runtime.frames" && r.Action == "start").Body["tracks"]!.AsArray();
            check(mapping.Count == 1 && mapping[0]!["id"]!.GetValue<string>() == selected,
                "A one-participant source capture requested unselected tracks");
            f.TerminalFrame = true; f.Refresh();
            await Until(() => f.EngineRequests.Any(r => r.Action == "ack-episode") || !f.GroupActive, "The single participant never acknowledged its whole episode");
            check(f.GroupActive && f.EngineRequests.Count(r => r.Action == "ack-episode") == 1,
                "Single-participant phase completion did not use the shared episode acknowledgement");
        }
        foreach (string delayed in new[] { "arm-start", "pika-start" })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.Rpc.DelayedOperation = delayed;
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Late-admission fixture did not arm");
            f.RequestGroupPlay(); await f.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var old = f.Get<object>("groupSimulation");
            Task stop = f.ClickSidebarGroupStop();
            await Task.Delay(50);
            check(f.GroupActive && ReferenceEquals(old, f.Get<object>("groupSimulation")) && !f.EngineRequests.Any(r => r.Action == "activate-start"),
                "An in-flight admission released its identity or activated after Stop before the acknowledgement retired");
            f.Rpc.ReleaseDelayed.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => !f.GroupActive, "A late admission did not drain and retire");
            var sent = f.Rpc.Events.ToArray();
            int accepted = Array.IndexOf(sent, "accepted:" + delayed);
            check(accepted >= 0 && sent.Skip(accepted + 1).Any(e => e == "send:hold") &&
                (delayed != "pika-start" || sent.Skip(accepted + 1).Any(e => e == "send:pika-hold")) && f.Mode == "pause",
                "A late accepted arm/Pika start escaped final holds and paused simulation");
            check(!f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() is "disable" or "reset" || r.Route == "/api/pika/disable"),
                "Simulation cleanup changed motor power or reset a controller");
        }
        foreach (var (name, change) in new (string, Action<Fixture>)[] {
            ("invalid model", f => f.InvalidModel = true),
            ("unsettled first frame", f => f.UnsettledStart = true),
            ("changed activation frame", f => f.ChangeActivationFrame = true),
            ("source cursor gap", f => f.FrameCursorGap = true),
            ("expired source", f => f.FrameSourceAgeMs = 260),
            ("arm heartbeat failure", f => f.Rpc.RejectHeartbeatAfterAccepted = true),
            ("wrong trajectory transport ACK", f => f.Rpc.TransportAcknowledgementOverride = SimulationTrajectoryTransport.StreamedPosition)
        })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher(); change(f);
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "A boundary fixture did not arm: " + name);
            var session = f.Get<object>("groupSimulation");
            f.RequestGroupPlay(); await Until(() => !f.GroupActive, "A bad runtime boundary retained following: " + name);
            check(f.Mode == "pause" && f.Get<string>("uiActionError").Length > 0 && f.SidebarStatus().Contains("Following stopped"),
                "Runtime failure lost its original error or final pause: " + name);
            var lane = ((Array)session.GetType().GetField("Lanes")!.GetValue(session)!).GetValue(0)!;
            var diagnostic = (SimulationFollowDiagnostics)lane.GetType().GetField("Diagnostics")!.GetValue(lane)!;
            await diagnostic.Completion;
            check(File.Exists(diagnostic.Pathname) && diagnostic.FirstReason.Length > 0 &&
                !File.ReadAllText(diagnostic.Pathname).Contains("fixture-backend", StringComparison.Ordinal),
                "The sole executor lost its scoped first-failure diagnostic or leaked its backend token: " + name);
            check(!f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() is "disable" or "reset"),
                "A failed source boundary reset the arm or removed holding power: " + name);
        }
        await using (var stale = new Fixture())
        {
            stale.ConfigureGroup(); stale.UseUiDispatcher(); await stale.Click("group.follow.toggle");
            await Until(() => stale.GroupArmedForPlay, "Stale-receipt fixture did not arm");
            stale.EnvelopeAgeMs = 600; stale.Refresh(); stale.FreezeTelemetry = true;
            await Until(() => !stale.GroupActive, "A stale controller receipt retained its armed preparation");
            check(stale.Mode == "edit" && stale.Rpc.Requests.IsEmpty, "Stale Edit preparation entered Play or sent a hardware command");
        }
        await using (var lost = new Fixture())
        {
            lost.ConfigureGroup(); lost.SelectGroupFollowers("piper-1"); lost.UseUiDispatcher();
            lost.Rpc.DelayedOperation = "arm-start"; lost.Rpc.LostAcknowledgement = true;
            await lost.Click("group.follow.toggle"); await Until(() => lost.GroupArmedForPlay, "Lost-ACK fixture did not arm");
            lost.RequestGroupPlay(); await lost.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            lost.Rpc.ReleaseDelayed.TrySetResult(); await Until(() => !lost.GroupActive, "A lost admission acknowledgement retained or replayed the request");
            check(lost.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 1 &&
                lost.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold") && lost.Mode == "pause",
                "A lost acknowledgement skipped its post-retirement hold or retried the admission");
        }
        foreach (bool sourceMiss in new[] { false, true })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.ScriptedMissedGrasp = true; f.ScriptedSourceMiss = sourceMiss;
            foreach (var joint in f.Snapshot["devices"]![0]!["joints"]!.AsArray()) { joint!["actual_deg"] = 0d; joint["command_deg"] = 0d; }
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Missed-grasp fixture did not arm");
            f.RequestGroupPlay(); await Until(() => !f.GroupActive, "A missed grasp failed to complete release and home");
            var openings = f.Rpc.Requests.Where(r => r.Route == "/api/pika/follow-update").Select(r => r.Body["opening_mm"]!.GetValue<double>()).ToArray();
            check(f.Get<string>("GroupFollowStatus").Contains("Grasp not confirmed") && !f.EngineRequests.Any(r => r.Action == "ack-episode") &&
                openings.Contains(10) && openings.Last() == 20 && f.Mode == "pause" && f.Snapshot["devices"]![0]!["joints"]!.AsArray().All(j => j!["enabled"]!.GetValue<bool>()),
                "Source/physical grasp failure skipped home, claimed success, or disabled the arm");
        }
        await using (var jaws = new Fixture())
        {
            jaws.ConfigureGroup(); jaws.SelectGroupFollowers("piper-1"); jaws.UseUiDispatcher(); jaws.Rpc.RejectIdleHold = true;
            jaws.Snapshot["pikas"]![0]!["telemetry"]!["motor"]!["Position"] = LuckyRobots.Piper.Pika.PikaProtocol.Angle(30);
            jaws.DelayedRuntimeAction = "mark-start-ready";
            await jaws.Click("group.follow.toggle"); await Until(() => jaws.GroupArmedForPlay, "Pika-only matching fixture did not arm");
            jaws.RequestGroupPlay(); await jaws.RuntimeReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stop = jaws.ClickSidebarGroupStop(); jaws.ReleaseRuntime.TrySetResult(); await stop;
            check(!jaws.GroupActive && !jaws.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold") &&
                jaws.Rpc.Requests.Any(r => r.Route == "/api/pika/hold"), "Pika-only matching emitted an unsupported hold on an untouched idle arm");
        }
        foreach (bool both in new[] { false, true })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Pika-audit fixture did not arm");
            var group = f.Get<object>("groupSimulation");
            f.RequestGroupPlay(); await Until(() => f.GroupStage == "following", "Pika-audit fixture did not start");
            var stream = new PikaFollowSession(_ => { }); string id = stream.Start(0); stream.Update(id, 12, 20, .01); stream.Tick(.27, PikaProtocol.Angle(20), true);
            f.Rpc.FailArmWithPikaAudit = both; f.Rpc.PikaFailureAudit = stream.Audit;
            await Until(() => !f.GroupActive, "A negative arm/Pika ACK retained publication authority");
            var lane = ((Array)group.GetType().GetField("Lanes")!.GetValue(group)!).GetValue(0)!;
            var diagnostic = (SimulationFollowDiagnostics)lane.GetType().GetField("Diagnostics")!.GetValue(lane)!; await diagnostic.Completion;
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(diagnostic.Pathname))!["firstFailure"]!;
            check(saved["Hardware"]!["Pika"]!["FollowAudit"]!["LastRetirement"]!["Cause"]!.GetValue<string>() == "input-expired" &&
                saved["Hardware"]!["Pika"]!["FollowAudit"]!["LastRetirement"]!["LastInputSequence"]!.GetValue<long>() == 12,
                "An independent negative Pika ACK audit was overwritten by the arm failure or older telemetry");
            check(saved["Publications"]!["RecentPublications"]!.AsArray().Any(p => p!["PikaErrorType"]?.GetValue<string>() == nameof(PikaFollowRejectedException)) &&
                f.Rpc.Requests.Count(r => r.Route == "/api/pika/follow-start") == 1 && f.Mode == "pause",
                "Diagnostics lost per-channel timing, restarted following or defeated final holding");
        }
        await using (var disconnected = new Fixture())
        {
            disconnected.ConfigureGroup(); disconnected.UseUiDispatcher(); await disconnected.Click("group.follow.toggle");
            await Until(() => disconnected.GroupArmedForPlay, "Disconnected-stream fixture did not arm");
            disconnected.Rpc.StreamUnavailable = true; await disconnected.Gap("ReadTelemetry");
            await Until(() => !disconnected.GroupActive, "A global telemetry disconnect retained grouped authority");
            check(disconnected.Mode == "edit" && disconnected.Rpc.Requests.IsEmpty,
                "A disconnected Edit preparation sent hardware targets or entered Play");
        }
        foreach (string broken in new[] { "devices", "pikas" })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.UseUiDispatcher();
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Malformed-inventory fixture did not arm");
            f.RequestGroupPlay(); await Until(() => f.GroupStage == "following", "Malformed-inventory fixture did not start");
            f.Snapshot[broken]!.AsArray().Add(f.Snapshot[broken]![0]!.DeepClone()); f.Refresh();
            await Until(() => !f.GroupActive, "An ambiguous lane prevented whole-session retirement");
            check(f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold" && r.Body["device_id"]?.GetValue<string>() == "can-b") &&
                f.Rpc.Requests.Any(r => r.Route == "/api/pika/hold" && r.Body["pika_device"]?.GetValue<string>() == "/dev/pika-b"),
                "One lane's ambiguous evidence suppressed its healthy partner's independent arm/Pika holds: " + broken);
            check(f.EngineRequests.Any(r => r.Action == "cancel-start") && f.EngineRequests.Any(r => r.Action == "pause") && f.Mode == "pause",
                "An evidence lookup failure skipped engine cancel/pause: " + broken);
            check(!f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() is "reset" or "disable"),
                "Malformed release evidence reset or disabled another participant");
        }
        await using (var discovery = new Fixture())
        {
            discovery.ConfigureGroup(); discovery.SelectGroupFollowers("piper-2"); discovery.UseUiDispatcher(); discovery.LinkEditorDelayMilliseconds = 180;
            await discovery.Click("group.follow.toggle");
            await Until(() => discovery.EngineRequests.Any(r => r.Method == "scene.robots"), "Read-only discovery did not start");
            await discovery.Click("stop"); await Task.Delay(250);
            check(!discovery.GroupActive && !discovery.EngineRequests.Any(r => r.Action == "arm-next-play") &&
                !discovery.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() is "pose" or "follow-start" or "follow-update"),
                "STOP during discovery later armed the unchecked/view-independent participant");
        }
        foreach (string action in new[] { "arm-next-play", "activate-start" })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.DelayedRuntimeAction = action;
            await f.Click("group.follow.toggle");
            if (action == "activate-start") { await Until(() => f.GroupArmedForPlay, "Delayed activation did not arm"); f.RequestGroupPlay(); }
            await f.RuntimeReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            object original = f.Get<object>("groupSimulation");
            Task stop = f.ClickSidebarGroupStop(); await Task.Delay(50);
            check(f.GroupActive && ReferenceEquals(original, f.Get<object>("groupSimulation")),
                "A pending editor ACK released the preparation identity before retiring: " + action);
            f.ReleaseRuntime.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => !f.GroupActive, "A late editor ACK failed to finish cleanup: " + action);
            check(f.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 && f.EngineRequests.Any(r => r.Action == "cancel-start") &&
                f.Mode == (action == "arm-next-play" ? "edit" : "pause"),
                "A late editor ACK revived Play, admitted a replacement or escaped final cancellation: " + action);
            check(action != "arm-next-play" || f.Rpc.Requests.IsEmpty,
                "Cancelled Edit arming sent hardware targets after its delayed ACK");
        }
        foreach (bool publish in new[] { true, false })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.DelayNativeAdmissionTelemetry = true;
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Native-reference fixture did not arm");
            f.RequestGroupPlay(); await Until(() => f.GroupStage == "admitting" && f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "follow-update"),
                "Native-reference admission was not primed at the frozen first frame");
            await Task.Delay(40);
            check(f.GroupActive && f.Mode == "pause" && !f.EngineRequests.Any(r => r.Action == "activate-start"),
                "A successful native start ACK activated Play using cached pre-admission telemetry");
            if (publish)
            {
                f.PublishNativeAdmissionReference(); await Until(() => f.GroupStage == "following" || !f.GroupActive, "Fresh native-reference evidence did not release admission");
                check(f.GroupActive && f.EngineRequests.Count(r => r.Action == "activate-start") == 1,
                    "An exact fresh native reference failed to release the existing admission once");
            }
            else
            {
                await Until(() => !f.GroupActive, "Missing native-reference evidence retained admission indefinitely");
                check(f.Mode == "pause" && !f.EngineRequests.Any(r => r.Action == "activate-start") && f.Get<string>("uiActionError").Contains("fresh admitted reference"),
                    "A native-reference deadline resumed playback or concealed the exact admission failure");
            }
            check(f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 1,
                "Native-reference waiting retried an acknowledged motion admission");
        }
    }
}

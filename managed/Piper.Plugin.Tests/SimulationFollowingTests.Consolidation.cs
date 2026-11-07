using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;
using LuckyRobots.Piper.Pika;

internal static partial class SimulationFollowingTests
{
    internal static async Task RunUnifiedFollowing(Action<bool, string> check)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        static PipersPlugin Plugin(Fixture fixture) => (PipersPlugin)typeof(Fixture).GetField("plugin", fields)!.GetValue(fixture)!;
        static async Task Until(Func<bool> condition, string reason)
        {
            long began = Stopwatch.GetTimestamp();
            while (!condition())
            { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5) throw new InvalidOperationException(reason); await Task.Delay(10); }
        }
        foreach (bool supportedRecovery in new[] { false, true })
        {
            await using var f = new Fixture();
            f.ConfigureGroup(); f.SelectGroupFollowers("piper-2"); f.UseUiDispatcher();
            var plugin = Plugin(f);
            var authority = plugin.CommandAuthority;
            object? recovery = null;
            if (supportedRecovery)
            {
                var kind = Enum.Parse(typeof(PipersPlugin).GetNestedType("RecoveryKind", BindingFlags.NonPublic)!, "HoldHere");
                recovery = typeof(PipersPlugin).GetMethod("BeginRecovery", fields)!.Invoke(plugin,
                    [kind, f.Snapshot["devices"]![0]!.AsObject(), authority.Epoch, CancellationToken.None]);
            }
            else authority.TryAcknowledge(authority.Epoch, new("piper-1", f.Get<string>("token"), "can-a", 7), 77);
            try
            {
                await f.Click("group.follow.toggle");
                await Until(() => supportedRecovery ? f.Get<string>("message").StartsWith("Recovery is in progress", StringComparison.Ordinal) :
                    f.Get<string>("groupFollowFailure").Length > 0, "A conflicting ordinary operation did not block group preparation");
                check(!f.GroupActive && f.SidebarStatus().Contains(supportedRecovery ? "recovery" : "First", StringComparison.Ordinal) &&
                    !f.EngineRequests.Any(r => r.Action == "arm-next-play") && f.Rpc.Requests.IsEmpty,
                    "Follow armed alongside a tracked ordinary arm lease or supported recovery on an unchecked robot");
            }
            finally
            {
                if (recovery is not null) typeof(PipersPlugin).GetMethod("RetireRecovery", fields)!.Invoke(plugin, [recovery]);
            }
        }
        await using (var f = new Fixture())
        {
            f.ConfigureGroup(); f.UseUiDispatcher(); f.LinkEditorDelayMilliseconds = 180; f.DelayedRuntimeAction = "arm-next-play";
            await f.Click("group.follow.toggle");
            await Until(() => f.EngineRequests.Any(r => r.Method == "scene.robots"), "Pending-control fixture did not start Follow discovery");
            check(!f.GroupActive, "Follow discovery installed ownership before its inventory returned");
            await f.Click("joint.6.plus"); // Stamped while the read-only discovery still owns the actor.
            await f.RuntimeReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await f.Click("pika.zero"); // Arrived after Follow owned control and must never be queued.
            await f.ClickSidebarGroupStop();
            f.ReleaseRuntime.TrySetResult();
            await Until(() => !f.GroupActive && f.DiscardedUiEvents.Any(input => input.WidgetId.EndsWith(".joint.6.plus", StringComparison.Ordinal)),
                "A queued jog was not discarded at the Follow ownership boundary");
            check(f.Rpc.Requests.IsEmpty && !f.DiscardedUiEvents.Any(input => input.WidgetId.EndsWith(".pika.zero", StringComparison.Ordinal)),
                "A stale queued or active-group manual target reached transport after retirement");
        }
        await using (var f = new Fixture())
        {
            f.ConfigureGroup(); f.SelectGroupFollowers("piper-2"); f.UseUiDispatcher();
            long before = f.Get<long>("groupControlEpoch");
            await f.GroupEvent("toggle");
            var authority = Plugin(f).CommandAuthority;
            authority.TryAcknowledge(authority.Epoch, new("piper-1", f.Get<string>("token"), "can-a", 7), 77);
            var motion = f.Snapshot["devices"]![0]!["motion"]!;
            motion["active"] = true; motion["request_id"] = 77L; motion["owner"] = f.Get<string>("owner");
            f.FocusGroupSecond();
            await f.Gap("ArmHeartbeats");
            check(f.GroupArmedForPlay && f.Get<long>("groupControlEpoch") == before + 1 &&
                f.Rpc.Requests.Any(r => r.Route == "/api/heartbeat" && r.Body["device_id"]?.GetValue<string>() == "can-a" &&
                    r.Body["generation"]?.GetValue<long>() == 7 && r.Body["request_id"]?.GetValue<long>() == 77),
                "An armed follower suppressed an independent ordinary heartbeat or rebound it to the viewed robot");
            f.RequestGroupPlay();
            await Until(() => !f.GroupActive, "An independent lease appearing after arming did not block physical admission");
            check(f.Get<long>("groupControlEpoch") == before + 2 &&
                !f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() is "pose" or "follow-start" or "follow-update"),
                "Retired group ownership failed to fence stale controls or admitted hardware alongside the ordinary lease");
        }
        await using (var f = new Fixture())
        {
            f.ConfigureGroup(); f.Rpc.FailedPikaHeartbeatDevice = "/dev/pika-b";
            using var stopHeartbeats = new CancellationTokenSource();
            var worker = (Task)typeof(PipersPlugin).GetMethod("PikaHeartbeats", fields)!.Invoke(Plugin(f), [stopHeartbeats.Token])!;
            int Renewed(string device) => f.Rpc.Requests.Count(r => r.Route == "/api/pika/heartbeat" &&
                r.Body["pika_device"]?.GetValue<string>() == device);
            try
            {
                await f.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Until(() => Renewed("/dev/pika-a") >= 2, "A stalled Pika heartbeat blocked the healthy gripper's next cadence");
                check(Renewed("/dev/pika-b") == 1, "A stalled gripper accumulated duplicate heartbeat requests");
                f.Rpc.ReleaseDelayed.TrySetResult();
                await Until(() => Renewed("/dev/pika-a") >= 3, "A failed Pika heartbeat retired the healthy gripper's renewal");
                check(f.Get<bool>("pikaOwned"), "A partner Pika heartbeat failure cleared ownership of the healthy selected gripper");
            }
            finally
            {
                stopHeartbeats.Cancel();
                f.Rpc.ReleaseDelayed.TrySetResult();
                try { await worker; } catch (OperationCanceledException) { }
            }
        }
        await using (var f = new Fixture(followClock: new FollowTestClock()))
        {
            f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            await f.GroupEvent("toggle");
            var plugin = Plugin(f);
            var original = f.Session!;
            var sessionType = original.GetType();
            var lanes = (Array)sessionType.GetField("Lanes")!.GetValue(original)!;
            await f.ClickSidebarGroupStop();
            await Until(() => !f.GroupActive, "Write-expiry fixture did not retire its initial Edit latch");
            var clock = (FollowTestClock)f.Get<TimeProvider>("followClock");
            var session = Activator.CreateInstance(sessionType, fields | BindingFlags.Public, null,
                [f.Get<string>("token"), "source-a", "robot-a", "asset-a", "model-a", lanes, f.Get<long>("groupInputEpoch"), clock], null)!;
            var advance = sessionType.GetMethod("Advance")!;
            foreach (var phase in new[] { GroupFollowPhase.Armed, GroupFollowPhase.Matching, GroupFollowPhase.Admitting,
                GroupFollowPhase.Activating, GroupFollowPhase.DrainingPrimer, GroupFollowPhase.Following })
                advance.Invoke(session, [phase]);
            sessionType.GetField("EnvelopeTicks")!.SetValue(session, clock.GetTimestamp());
            clock.Advance(TimeSpan.FromMilliseconds(251));
            f.Refresh(); // Hardware evidence remains fresh while the already-planned source batch expires.
            var groupField = typeof(PipersPlugin).GetField("groupSimulation", fields)!;
            groupField.SetValue(plugin, session);
            bool rejected = false;
            try
            {
                var targets = new Dictionary<string, OrderedSimulationTarget> { ["piper-1"] = new(new double[6], 20) };
                await (Task)typeof(PipersPlugin).GetMethod("SendGroupTargets", fields)!.Invoke(plugin,
                    [session, targets, CancellationToken.None, false])!;
            }
            catch (InvalidOperationException error) when (error.Message.Contains("source expired after 250 ms", StringComparison.Ordinal))
            { rejected = true; }
            finally { groupField.SetValue(plugin, null); }
            check(rejected && f.Rpc.Requests.IsEmpty,
                "An ordered batch reached the arm after its source lease expired between planning and transport");
        }
        await using (var f = new Fixture())
        {
            f.ConfigureGroup(); f.UseUiDispatcher(); f.DelayedRuntimeAction = "cancel-start";
            var plugin = Plugin(f);
            var sessionType = typeof(PipersPlugin).GetNestedType("GroupSession", BindingFlags.NonPublic)!;
            var lanes = Array.CreateInstance(sessionType.GetField("Lanes")!.FieldType.GetElementType()!, 0);
            // Expose the real publication window without a production timing hook.
            var session = Activator.CreateInstance(sessionType, fields | BindingFlags.Public, null,
                [f.Get<string>("token"), "source-a", "robot-a", "asset-a", "model-a", lanes, f.Get<long>("groupInputEpoch"),
                    f.Get<TimeProvider>("followClock")], null);
            typeof(PipersPlugin).GetField("groupSimulation", fields)!.SetValue(plugin, session);
            bool activationRejected = false;
            try
            {
                await (Task)typeof(PipersPlugin).GetMethod("GroupEngine", fields)!.Invoke(plugin,
                    [session, "robot.runtime.control", "activate-start", CancellationToken.None, null, false])!;
            }
            catch (InvalidOperationException error) when (error.Message.Contains("while Follow is Arming", StringComparison.Ordinal))
            { activationRejected = true; }
            Task stop = f.ClickSidebarGroupStop();
            bool returnedBeforeWorker = stop.IsCompletedSuccessfully && f.GroupPhase == GroupFollowPhase.Retiring && ReferenceEquals(f.Session, session);
            bool lateArmingRejected = false;
            try { sessionType.GetMethod("Advance")!.Invoke(session, [GroupFollowPhase.Armed]); }
            catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException) { lateArmingRejected = true; }
            var worker = (Task)typeof(PipersPlugin).GetMethod("RunGroupSimulation", fields)!.Invoke(plugin, [session])!;
            await f.RuntimeReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task retry = f.GroupEvent("toggle");
            bool admissionWaited = !retry.IsCompleted && ReferenceEquals(f.Session, session);
            f.ReleaseRuntime.TrySetResult();
            await Task.WhenAll(stop, retry, worker).WaitAsync(TimeSpan.FromSeconds(2));
            check(activationRejected && lateArmingRejected && !f.EngineRequests.Any(r => r.Action is "arm-next-play" or "activate-start"),
                "An illegal activation or late arm acknowledgement advanced the Follow phase");
            check(returnedBeforeWorker && admissionWaited && !f.GroupActive && f.Rpc.Requests.IsEmpty &&
                (GroupFollowPhase)sessionType.GetProperty("Phase")!.GetValue(session)! == GroupFollowPhase.Retired &&
                f.EngineRequests.Count(r => r.Action == "cancel-start") == 1,
                "Stop blocked the dispatcher or another Follow bypassed the original group's retirement barrier");
        }
        foreach (string selected in new[] { "piper-1", "piper-2" })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.SelectGroupFollowers(selected); f.UseUiDispatcher();
            await f.Click("group.follow.toggle");
            await Until(() => f.GroupArmedForPlay, "A single selected participant did not arm in Edit");
            check(f.Mode == "edit" && f.Rpc.Requests.IsEmpty && f.EngineRequests.Count(r => r.Action == "arm-next-play") == 1,
                "Single-participant arming entered Play or sent a target before user Play");
            f.RequestGroupPlay();
            await Until(() => f.GroupPhase == GroupFollowPhase.Following || !f.GroupActive, "One grouped participant did not start");
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
        foreach (bool expiredPrimer in new[] { false, true })
        {
            var clock = new FollowTestClock();
            await using var f = new Fixture(followClock: clock);
            f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.Rpc.DelayedOperation = "follow-update";
            await f.Click("group.follow.toggle");
            await Until(() => f.GroupArmedForPlay, "Primer-drain fixture did not arm");
            f.RequestGroupPlay();
            await f.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => f.GroupPhase == GroupFollowPhase.DrainingPrimer || !f.GroupActive,
                "Activation did not enter the explicit primer drain");
            bool waited = f.GroupPhase == GroupFollowPhase.DrainingPrimer &&
                f.EngineRequests.Count(r => r.Action == "activate-start") == 1 &&
                !f.EngineRequests.Any(r => r.Method == "robot.runtime.frames" && r.Action.Length == 0) &&
                f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update") == 1;
            if (expiredPrimer) { clock.Advance(TimeSpan.FromMilliseconds(300)); f.Refresh(); }
            f.Rpc.ReleaseDelayed.TrySetResult();
            await Until(() => f.GroupPhase == GroupFollowPhase.Following || !f.GroupActive,
                "Retiring the priming update did not release ordered publication");
            check(waited, "Ordered publication overlapped the pending priming acknowledgement");
            if (expiredPrimer)
                check(!f.GroupActive && f.SidebarStatus().Contains("source expired after 250 ms", StringComparison.Ordinal) &&
                    !f.EngineRequests.Any(r => r.Method == "robot.runtime.frames" && r.Action.Length == 0) &&
                    f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update") == 1 &&
                    f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold"),
                    "A late primer ACK granted a fresh source lease or escaped retirement holds");
            else check(f.GroupPhase == GroupFollowPhase.Following,
                "A valid priming ACK retired the active group before ordered publication");
        }
        foreach (string delayed in new[] { "arm-start", "pika-start" })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.Rpc.DelayedOperation = delayed;
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Late-admission fixture did not arm");
            f.RequestGroupPlay(); await f.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var old = f.Get<object>("groupSimulation");
            await f.ClickSidebarGroupStop().WaitAsync(TimeSpan.FromSeconds(1));
            check(f.GroupPhase == GroupFollowPhase.Retiring && ReferenceEquals(old, f.Session) && !f.EngineRequests.Any(r => r.Action == "activate-start"),
                "Stop blocked the dispatcher, or an in-flight admission released its identity or activated after Stop");
            f.Rpc.ReleaseDelayed.TrySetResult();
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
        foreach (bool matchArm in new[] { false, true })
        {
            await using var jaws = new Fixture();
            jaws.ConfigureGroup(); jaws.SelectGroupFollowers("piper-1"); jaws.UseUiDispatcher(); jaws.Rpc.RejectIdleHold = true;
            jaws.Snapshot["pikas"]![0]!["telemetry"]!["motor"]!["Position"] = LuckyRobots.Piper.Pika.PikaProtocol.Angle(30);
            if (matchArm)
            {
                jaws.Snapshot["devices"]![0]!["joints"]![0]!["actual_deg"] = 5d;
                var accepted = jaws.Rpc.Accepted;
                jaws.Rpc.Accepted = (route, body) =>
                {
                    if (route == "/api/command" && body["action"]?.GetValue<string>() == "pose")
                    {
                        var arm = jaws.Snapshot["devices"]![0]!;
                        for (int i = 0; i < 6; i++) arm["joints"]![i]!["actual_deg"] = body["degrees"]![i]!.DeepClone();
                        arm["motion"]!["request_id"] = 77L;
                        arm["motion"]!["active"] = false;
                    }
                    accepted?.Invoke(route, body);
                };
            }
            jaws.DelayedRuntimeAction = "mark-start-ready";
            await jaws.Click("group.follow.toggle"); await Until(() => jaws.GroupArmedForPlay, "Pika-only matching fixture did not arm");
            jaws.RequestGroupPlay(); await jaws.RuntimeReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await jaws.ClickSidebarGroupStop().WaitAsync(TimeSpan.FromSeconds(1));
            jaws.ReleaseRuntime.TrySetResult();
            await Until(() => !jaws.GroupActive, "Start-matching cancellation did not retire");
            check(!jaws.GroupActive && !jaws.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold") &&
                jaws.Rpc.Requests.Any(r => r.Route == "/api/pika/hold") &&
                jaws.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "pose") == matchArm &&
                jaws.SidebarStatus().Contains(matchArm ? "First: arm already idle, gripper hold acknowledged" : "First: arm untouched, gripper hold acknowledged"),
                "Start matching failed to distinguish measured idle from an untouched arm or issued an unnecessary hold");
        }
        foreach (bool both in new[] { false, true })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Pika-audit fixture did not arm");
            var group = f.Get<object>("groupSimulation");
            f.RequestGroupPlay(); await Until(() => f.GroupPhase == GroupFollowPhase.Following, "Pika-audit fixture did not start");
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
            f.RequestGroupPlay(); await Until(() => f.GroupPhase == GroupFollowPhase.Following, "Malformed-inventory fixture did not start");
            f.Snapshot[broken]!.AsArray().Add(f.Snapshot[broken]![0]!.DeepClone()); f.Refresh();
            await Until(() => !f.GroupActive, "An ambiguous lane prevented whole-session retirement");
            check(f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold" && r.Body["device_id"]?.GetValue<string>() == "can-b") &&
                f.Rpc.Requests.Any(r => r.Route == "/api/pika/hold" && r.Body["pika_device"]?.GetValue<string>() == "/dev/pika-b"),
                "One lane's ambiguous evidence suppressed its healthy partner's independent arm/Pika holds: " + broken);
            check(f.EngineRequests.Any(r => r.Action == "cancel-start") && f.EngineRequests.Any(r => r.Action == "pause") && f.Mode == "pause",
                "An evidence lookup failure skipped engine cancel/pause: " + broken);
            string first = broken == "devices" ? "First: arm hold unverified, gripper hold acknowledged" : "First: arm hold acknowledged, gripper hold unverified";
            check(f.SidebarStatus().Contains(first) && f.SidebarStatus().Contains("Second: arm hold acknowledged, gripper hold acknowledged"),
                "The retirement result conflated a failed channel with its acknowledged partner holds: " + broken);
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
        foreach (var (action, backendChanged) in new[] { ("arm-next-play", false), ("activate-start", false), ("activate-start", true) })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.DelayedRuntimeAction = action;
            await f.Click("group.follow.toggle");
            if (action == "activate-start") { await Until(() => f.GroupArmedForPlay, "Delayed activation did not arm"); f.RequestGroupPlay(); }
            await f.RuntimeReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            object original = f.Get<object>("groupSimulation");
            if (backendChanged) typeof(PipersPlugin).GetField("token", fields)!.SetValue(Plugin(f), "replacement-backend");
            await f.ClickSidebarGroupStop().WaitAsync(TimeSpan.FromSeconds(1));
            check(f.GroupPhase == GroupFollowPhase.Retiring && ReferenceEquals(original, f.Session),
                "Stop blocked the dispatcher or a pending editor ACK released the preparation identity before retiring: " + action);
            f.ReleaseRuntime.TrySetResult();
            await Until(() => !f.GroupActive, "A late editor ACK failed to finish cleanup: " + action);
            check(f.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 && f.EngineRequests.Any(r => r.Action == "cancel-start") &&
                f.Mode == (action == "arm-next-play" ? "edit" : "pause"),
                "A late editor ACK revived Play, admitted a replacement or escaped final cancellation: " + action);
            check(action != "arm-next-play" || f.Rpc.Requests.IsEmpty,
                "Cancelled Edit arming sent hardware targets after its delayed ACK");
            string expected = action == "arm-next-play" ? "First: arm untouched, gripper untouched" :
                backendChanged ? "First: arm hold unverified, gripper hold unverified" : "First: arm hold acknowledged, gripper hold acknowledged";
            check(f.SidebarStatus().Contains(expected) && (!backendChanged || !f.Rpc.Requests.Any(r =>
                r.Body["action"]?.GetValue<string>() == "hold" || r.Route == "/api/pika/hold")),
                "The sidebar lost a release outcome or a replacement backend received the prior session's hold: " + action);
        }
        foreach (bool publish in new[] { true, false })
        {
            await using var f = new Fixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.DelayNativeAdmissionTelemetry = true;
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Native-reference fixture did not arm");
            f.RequestGroupPlay(); await Until(() => f.GroupPhase == GroupFollowPhase.Admitting && f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "follow-update"),
                "Native-reference admission was not primed at the frozen first frame");
            await Task.Delay(40);
            check(f.GroupActive && f.Mode == "pause" && !f.EngineRequests.Any(r => r.Action == "activate-start"),
                "A successful native start ACK activated Play using cached pre-admission telemetry");
            if (publish)
            {
                f.PublishNativeAdmissionReference(); await Until(() => f.GroupPhase == GroupFollowPhase.Following || !f.GroupActive, "Fresh native-reference evidence did not release admission");
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

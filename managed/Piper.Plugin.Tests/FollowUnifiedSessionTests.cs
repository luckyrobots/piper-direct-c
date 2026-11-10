using System.Diagnostics;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;
using LuckyRobots.Piper.Pika;

internal static class FollowUnifiedSessionTests
{
    private static string DiagnosticPath(FollowFixture fixture, GroupSession session, string profileId) => Path.Combine(
        fixture.Plugin.engine.Start.SettingsDirectory, "diagnostics", "simulation-follow",
        session.PrepareId + "-" + profileId + ".json");

    internal static async Task RunUnifiedFollowing()
    {
        static Task Until(Func<bool> condition, string reason) => Eventually(condition, reason);
        foreach (bool supportedRecovery in new[] { false, true })
        {
            await using var f = new FollowFixture();
            f.ConfigureGroup(); f.SelectGroupFollowers("piper-2"); f.UseUiDispatcher();
            var plugin = f.Plugin;
            var authority = plugin.CommandAuthority;
            RecoveryOperation? recovery = null;
            if (supportedRecovery)
            {
                recovery = plugin.BeginRecovery(RecoveryKind.HoldHere, f.Snapshot["devices"]![0]!.AsObject(), authority.Epoch, CancellationToken.None);
            }
            else authority.TryAcknowledge(authority.Epoch, new("piper-1", f.Plugin.telemetry.BackendToken, "can-a", 7), 77);
            try
            {
                await f.Click("group.follow.toggle");
                await Until(() => supportedRecovery ? f.Plugin.message.StartsWith("Recovery is in progress", StringComparison.Ordinal) :
                    f.GroupSetupFailure().Length > 0, "A conflicting ordinary operation did not block group preparation");
                Check(!f.GroupActive && f.SidebarStatus().Contains(supportedRecovery ? "recovery" : "First", StringComparison.Ordinal) &&
                    !f.EngineRequests.Any(r => r.Action == "arm-next-play") && f.Rpc.Requests.IsEmpty,
                    "Follow armed alongside a tracked ordinary arm lease or supported recovery on an unchecked robot");
            }
            finally
            {
                if (recovery is not null) plugin.RetireRecovery(recovery);
            }
        }
        await using (var f = new FollowFixture())
        {
            f.ConfigureGroup(); f.UseUiDispatcher(); f.LinkEditorDelayMilliseconds = 180; f.DelayedRuntimeAction = "arm-next-play";
            await f.Click("group.follow.toggle");
            await Until(() => f.EngineRequests.Any(r => r.Method == "scene.robots"), "Pending-control fixture did not start Follow discovery");
            Check(!f.GroupActive, "Follow discovery installed ownership before its inventory returned");
            await f.Click("joint.6.plus"); // Stamped while the read-only discovery still owns the actor.
            await f.RuntimeReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await f.Click("pika.zero"); // Arrived after Follow owned control and must never be queued.
            await f.ClickSidebarGroupStop();
            f.ReleaseRuntime.TrySetResult();
            await Until(() => !f.GroupActive && f.DiscardedUiEvents.Any(input => input.WidgetId.EndsWith(".joint.6.plus", StringComparison.Ordinal)),
                "A queued jog was not discarded at the Follow ownership boundary");
            Check(f.Rpc.Requests.IsEmpty && !f.DiscardedUiEvents.Any(input => input.WidgetId.EndsWith(".pika.zero", StringComparison.Ordinal)),
                "A stale queued or active-group manual target reached transport after retirement");
        }
        await using (var f = new FollowFixture())
        {
            f.ConfigureGroup(); f.SelectGroupFollowers("piper-2"); f.UseUiDispatcher();
            long before = f.Plugin.follow.ControlEpoch;
            await f.GroupEvent("toggle");
            var authority = f.Plugin.CommandAuthority;
            authority.TryAcknowledge(authority.Epoch, new("piper-1", f.Plugin.telemetry.BackendToken, "can-a", 7), 77);
            f.Edit(snapshot =>
            {
                var motion = snapshot["devices"]![0]!["motion"]!;
                motion["active"] = true; motion["request_id"] = 77L; motion["owner"] = f.Plugin.owner;
            });
            f.FocusGroupSecond();
            await f.Gap(f.Plugin.ArmHeartbeats);
            Check(f.GroupArmedForPlay && f.Plugin.follow.ControlEpoch == before + 1 &&
                f.Rpc.Requests.Any(r => r.Route == "/api/heartbeat" && r.Body["device_id"]?.GetValue<string>() == "can-a" &&
                    r.Body["generation"]?.GetValue<long>() == 7 && r.Body["request_id"]?.GetValue<long>() == 77),
                "An armed follower suppressed an independent ordinary heartbeat or rebound it to the viewed robot");
            f.RequestGroupPlay();
            await Until(() => !f.GroupActive, "An independent lease appearing after arming did not block physical admission");
            Check(f.Plugin.follow.ControlEpoch == before + 2 &&
                !f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() is "pose" or "follow-start" or "follow-update"),
                "Retired group ownership failed to fence stale controls or admitted hardware alongside the ordinary lease");
        }
        await using (var f = new FollowFixture())
        {
            f.ConfigureGroup(); f.Rpc.FailedPikaHeartbeatDevice = "/dev/pika-b";
            using var stopHeartbeats = new CancellationTokenSource();
            var worker = f.Plugin.PikaHeartbeats(stopHeartbeats.Token);
            int Renewed(string device) => f.Rpc.Requests.Count(r => r.Route == "/api/pika/heartbeat" &&
                r.Body["pika_device"]?.GetValue<string>() == device);
            try
            {
                await f.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Until(() => Renewed("/dev/pika-a") >= 2, "A stalled Pika heartbeat blocked the healthy gripper's next cadence");
                Check(Renewed("/dev/pika-b") == 1, "A stalled gripper accumulated duplicate heartbeat requests");
                f.Rpc.ReleaseDelayed.TrySetResult();
                await Until(() => Renewed("/dev/pika-a") >= 3, "A failed Pika heartbeat retired the healthy gripper's renewal");
                Check(f.Plugin.telemetry.Pika.Role == PikaSessionRole.Owned, "A partner Pika heartbeat failure cleared ownership of the healthy selected gripper");
            }
            finally
            {
                stopHeartbeats.Cancel();
                f.Rpc.ReleaseDelayed.TrySetResult();
                try { await worker; } catch (OperationCanceledException) { }
            }
        }
        {
            var clock = new FollowTestClock();
            await using var f = new FollowFixture(followClock: clock);
            f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.FreezeClockAtActivation = true;
            f.Rpc.DelayedOperation = "follow-update";
            await f.Click("group.follow.toggle");
            await Until(() => f.GroupArmedForPlay, "Write-expiry fixture did not arm");
            f.RequestGroupPlay();
            int primingPikaUpdates = 0, advanced = 0;
            try
            {
                await f.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await Until(() => f.GroupPhase == GroupFollowPhase.DrainingPrimer || !f.GroupActive,
                    "Write-expiry fixture did not reach primer drain");
                long primingSequence = f.Rpc.Requests.Single(r => r.Body["action"]?.GetValue<string>() == "follow-update")
                    .Body["sequence"]!.GetValue<long>();
                primingPikaUpdates = f.Rpc.Requests.Count(r => r.Route == "/api/pika/follow-update");
                f.Rpc.Accepted += (route, body) =>
                {
                    if (route != "/api/command" || body["action"]?.GetValue<string>() != "follow-update" ||
                        body["sequence"]!.GetValue<long>() <= primingSequence) return;
                    if (Interlocked.Exchange(ref advanced, 1) == 0)
                    {
                        // Both targets have been planned. Expire the source during the
                        // arm send while keeping hardware telemetry fresh for the next send.
                        clock.Advance(TimeSpan.FromMilliseconds(250));
                        f.Refresh();
                    }
                };
            }
            finally { f.Rpc.ReleaseDelayed.TrySetResult(); }
            await Until(() => !f.GroupActive, "An expired source did not retire the follower");
            Check(advanced == 1 && f.Rpc.Requests.Count(r => r.Route == "/api/pika/follow-update") == primingPikaUpdates &&
                f.SidebarStatus().Contains("source expired after 250 ms", StringComparison.Ordinal) &&
                f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold") &&
                f.Rpc.Requests.Any(r => r.Route == "/api/pika/hold"),
                "The gripper target was sent at the source deadline or expiry skipped a retirement hold");
        }
        await using (var f = new FollowFixture())
        {
            f.ConfigureGroup(); f.DelayedRuntimeAction = "cancel-start";
            await f.Click("group.follow.toggle");
            await Until(() => f.GroupArmedForPlay, "The Edit latch did not arm before retirement");
            await f.ClickSidebarGroupStop().WaitAsync(TimeSpan.FromSeconds(1));
            await f.RuntimeReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var retry = f.Click("group.follow.toggle");
            try
            {
                Check(!retry.IsCompleted && f.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 &&
                    f.Mode == "edit" && f.Rpc.Requests.IsEmpty,
                    "Stop blocked the sidebar or a new Follow bypassed the pending engine retirement");
            }
            finally { f.ReleaseRuntime.TrySetResult(); }
            await retry.WaitAsync(TimeSpan.FromSeconds(2));
            var retired = f.EngineRequests.Single(r => r.Action == "cancel-start").Body;
            Check(!f.GroupActive && f.Rpc.Requests.IsEmpty && !retired.ContainsKey("runId") &&
                retired["prepareId"]?.GetValue<string>() == f.EngineRequests.Single(r => r.Action == "arm-next-play").Body["prepareId"]?.GetValue<string>(),
                "Retiring an Edit latch sent hardware requests or replaced its original preparation scope");
        }
        await using (var f = new FollowFixture())
        {
            f.ConfigureGroup(); f.WrongPlayPreparation = true;
            await f.Click("group.follow.toggle");
            await Until(() => f.GroupArmedForPlay, "The preparation mismatch fixture did not arm");
            f.RequestGroupPlay();
            await Until(() => !f.GroupActive, "A mismatched Play acknowledgement retained Follow");
            var retired = f.EngineRequests.Single(r => r.Action == "cancel-start").Body;
            Check(f.Rpc.Requests.IsEmpty && !retired.ContainsKey("runId") &&
                retired["prepareId"]?.GetValue<string>() == f.EngineRequests.Single(r => r.Action == "arm-next-play").Body["prepareId"]?.GetValue<string>() &&
                !f.EngineRequests.Any(r => r.Action is "pause" or "activate-start"),
                "A mismatched Play acknowledgement redirected cleanup or admitted hardware");
        }
        foreach (string selected in new[] { "piper-1", "piper-2" })
        {
            await using var f = new FollowFixture(); f.ConfigureGroup(); f.SelectGroupFollowers(selected); f.UseUiDispatcher();
            await f.Click("group.follow.toggle");
            await Until(() => f.GroupArmedForPlay, "A single selected participant did not arm in Edit");
            Check(f.Mode == "edit" && f.Rpc.Requests.IsEmpty && f.EngineRequests.Count(r => r.Action == "arm-next-play") == 1,
                "Single-participant arming entered Play or sent a target before user Play");
            f.RequestGroupPlay();
            await Until(() => f.GroupPhase == GroupFollowPhase.Following || !f.GroupActive, "One grouped participant did not start");
            Check(f.GroupActive && f.Mode == "play", "The shared executor rejected a single selected participant: " + f.Plugin.follow.Status.Headline);
            string can = selected == "piper-1" ? "can-a" : "can-b", pika = selected == "piper-1" ? "/dev/pika-a" : "/dev/pika-b";
            Check(f.Rpc.Requests.Where(r => r.Body["action"]?.GetValue<string>() == "follow-start").All(r => r.Body["device_id"]?.GetValue<string>() == can) &&
                f.Rpc.Requests.Where(r => r.Route == "/api/pika/follow-start").All(r => r.Body["pika_device"]?.GetValue<string>() == pika),
                "One participant's targets escaped to an unchecked arm or gripper");
            var mapping = f.EngineRequests.Single(r => r.Method == "robot.runtime.frames" && r.Action == "start").Body["tracks"]!.AsArray();
            Check(mapping.Count == 1 && mapping[0]!["id"]!.GetValue<string>() == selected,
                "A one-participant source capture requested unselected tracks");
            f.TerminalFrame = true; f.Refresh();
            await Until(() => f.EngineRequests.Any(r => r.Action == "ack-episode") || !f.GroupActive, "The single participant never acknowledged its whole episode");
            Check(f.GroupActive && f.EngineRequests.Count(r => r.Action == "ack-episode") == 1,
                "Single-participant phase completion did not use the shared episode acknowledgement");
        }
        foreach (bool expiredPrimer in new[] { false, true })
        {
            var clock = new FollowTestClock();
            await using var f = new FollowFixture(followClock: clock);
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
            Check(waited, "Ordered publication overlapped the pending priming acknowledgement");
            if (expiredPrimer)
                Check(!f.GroupActive && f.SidebarStatus().Contains("source expired after 250 ms", StringComparison.Ordinal) &&
                    !f.EngineRequests.Any(r => r.Method == "robot.runtime.frames" && r.Action.Length == 0) &&
                    f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update") == 1 &&
                    f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold"),
                    "A late primer ACK granted a fresh source lease or escaped retirement holds");
            else Check(f.GroupPhase == GroupFollowPhase.Following,
                "A valid priming ACK retired the active group before ordered publication");
        }
        foreach (string delayed in new[] { "arm-start", "pika-start" })
        {
            await using var f = new FollowFixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.Rpc.DelayedOperation = delayed;
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Late-admission fixture did not arm");
            f.RequestGroupPlay(); await f.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var old = f.Plugin.follow.Session;
            await f.ClickSidebarGroupStop().WaitAsync(TimeSpan.FromSeconds(1));
            Check(f.GroupPhase == GroupFollowPhase.Retiring && ReferenceEquals(old, f.Session) && !f.EngineRequests.Any(r => r.Action == "activate-start"),
                "Stop blocked the dispatcher, or an in-flight admission released its identity or activated after Stop");
            f.Rpc.ReleaseDelayed.TrySetResult();
            await Until(() => !f.GroupActive, "A late admission did not drain and retire");
            var sent = f.Rpc.Events.ToArray();
            int accepted = Array.IndexOf(sent, "accepted:" + delayed);
            Check(accepted >= 0 && sent.Skip(accepted + 1).Any(e => e == "send:hold") &&
                (delayed != "pika-start" || sent.Skip(accepted + 1).Any(e => e == "send:pika-hold")) && f.Mode == "pause",
                "A late accepted arm/Pika start escaped final holds and paused simulation");
            Check(!f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() is "disable" or "reset" || r.Route == "/api/pika/disable"),
                "Simulation cleanup changed motor power or reset a controller");
        }
        foreach (var (name, change) in new (string, Action<FollowFixture>)[] {
            ("invalid model", f => f.InvalidModel = true),
            ("unsettled first frame", f => f.UnsettledStart = true),
            ("changed activation frame", f => f.ChangeActivationFrame = true),
            ("source cursor gap", f => f.FrameCursorGap = true),
            ("expired source", f => f.FrameSourceAgeMs = 260),
            ("arm heartbeat failure", f => f.Rpc.RejectHeartbeatAfterAccepted = true),
            ("wrong trajectory transport ACK", f => f.Rpc.TransportAcknowledgementOverride = SimulationTrajectoryTransport.StreamedPosition)
        })
        {
            await using var f = new FollowFixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher(); change(f);
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "A boundary fixture did not arm: " + name);
            var session = f.Plugin.follow.Session!;
            f.RequestGroupPlay(); await Until(() => !f.GroupActive, "A bad runtime boundary retained following: " + name);
            Check(f.Mode == "pause" && f.Plugin.uiActionError.Length > 0 && f.SidebarStatus().Contains("Following stopped"),
                "Runtime failure lost its original error or final pause: " + name);
            await session.Lanes[0].Diagnostics!.Completion;
            string diagnosticPath = DiagnosticPath(f, session, "piper-1");
            Check(File.Exists(diagnosticPath) && JsonNode.Parse(File.ReadAllText(diagnosticPath))!["firstFailure"]!["Reason"]!.GetValue<string>().Length > 0 &&
                !File.ReadAllText(diagnosticPath).Contains("fixture-backend", StringComparison.Ordinal),
                "The sole executor lost its scoped first-failure diagnostic or leaked its backend token: " + name);
            Check(!f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() is "disable" or "reset"),
                "A failed source boundary reset the arm or removed holding power: " + name);
        }
        await using (var stale = new FollowFixture())
        {
            stale.ConfigureGroup(); stale.UseUiDispatcher(); await stale.Click("group.follow.toggle");
            await Until(() => stale.GroupArmedForPlay, "Stale-receipt fixture did not arm");
            stale.EnvelopeAgeMs = 600; stale.Refresh(); stale.FreezeTelemetry = true;
            await Until(() => !stale.GroupActive, "A stale controller receipt retained its armed preparation");
            Check(stale.Mode == "edit" && stale.Rpc.Requests.IsEmpty, "Stale Edit preparation entered Play or sent a hardware command");
        }
        await using (var lost = new FollowFixture())
        {
            lost.ConfigureGroup(); lost.SelectGroupFollowers("piper-1"); lost.UseUiDispatcher();
            lost.Rpc.DelayedOperation = "arm-start"; lost.Rpc.LostAcknowledgement = true;
            await lost.Click("group.follow.toggle"); await Until(() => lost.GroupArmedForPlay, "Lost-ACK fixture did not arm");
            lost.RequestGroupPlay(); await lost.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            lost.Rpc.ReleaseDelayed.TrySetResult(); await Until(() => !lost.GroupActive, "A lost admission acknowledgement retained or replayed the request");
            Check(lost.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 1 &&
                lost.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold") && lost.Mode == "pause",
                "A lost acknowledgement skipped its post-retirement hold or retried the admission");
        }
        foreach (bool sourceMiss in new[] { false, true })
        {
            await using var f = new FollowFixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.ScriptedMissedGrasp = true; f.ScriptedSourceMiss = sourceMiss;
            f.Edit(snapshot => { foreach (var joint in snapshot["devices"]![0]!["joints"]!.AsArray()) { joint!["actual_deg"] = 0d; joint["command_deg"] = 0d; } });
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Missed-grasp fixture did not arm");
            f.RequestGroupPlay(); await Until(() => !f.GroupActive, "A missed grasp failed to complete release and home");
            var openings = f.Rpc.Requests.Where(r => r.Route == "/api/pika/follow-update").Select(r => r.Body["opening_mm"]!.GetValue<double>()).ToArray();
            Check(f.Plugin.follow.Status.Headline.Contains("Grasp not confirmed") && !f.EngineRequests.Any(r => r.Action == "ack-episode") &&
                openings.Contains(10) && openings.Last() == 20 && f.Mode == "pause" && f.Snapshot["devices"]![0]!["joints"]!.AsArray().All(j => j!["enabled"]!.GetValue<bool>()),
                "Source/physical grasp failure skipped home, claimed success, or disabled the arm");
        }
        foreach (bool matchArm in new[] { false, true })
        {
            await using var jaws = new FollowFixture();
            jaws.ConfigureGroup(); jaws.SelectGroupFollowers("piper-1"); jaws.UseUiDispatcher(); jaws.Rpc.RejectIdleHold = true;
            jaws.Edit(snapshot => snapshot["pikas"]![0]!["telemetry"]!["motor"]!["Position"] = LuckyRobots.Piper.Pika.PikaProtocol.Angle(30));
            if (matchArm)
            {
                jaws.Edit(snapshot => snapshot["devices"]![0]!["joints"]![0]!["actual_deg"] = 5d);
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
            Check(!jaws.GroupActive && !jaws.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold") &&
                jaws.Rpc.Requests.Any(r => r.Route == "/api/pika/hold") &&
                jaws.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "pose") == matchArm &&
                jaws.SidebarStatus().Contains(matchArm ? "First: arm already idle, gripper hold acknowledged" : "First: arm untouched, gripper hold acknowledged"),
                "Start matching failed to distinguish measured idle from an untouched arm or issued an unnecessary hold");
        }
        foreach (bool both in new[] { false, true })
        {
            await using var f = new FollowFixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Pika-audit fixture did not arm");
            var group = f.Plugin.follow.Session!;
            f.RequestGroupPlay(); await Until(() => f.GroupPhase == GroupFollowPhase.Following, "Pika-audit fixture did not start");
            var stream = new PikaFollowSession(_ => { }); string id = stream.Start(0); stream.Update(id, 12, 20, .01); stream.Tick(.27, PikaProtocol.Angle(20), true);
            f.Rpc.FailArmWithPikaAudit = both; f.Rpc.PikaFailureAudit = stream.Audit;
            await Until(() => !f.GroupActive, "A negative arm/Pika ACK retained publication authority");
            await group.Lanes[0].Diagnostics!.Completion;
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(DiagnosticPath(f, group, "piper-1")))!["firstFailure"]!;
            Check(saved["Hardware"]!["Pika"]!["FollowAudit"]!["LastRetirement"]!["Cause"]!.GetValue<string>() == "input-expired" &&
                saved["Hardware"]!["Pika"]!["FollowAudit"]!["LastRetirement"]!["LastInputSequence"]!.GetValue<long>() == 12,
                "An independent negative Pika ACK audit was overwritten by the arm failure or older telemetry");
            Check(saved["Publications"]!["RecentPublications"]!.AsArray().Any(p => p!["PikaErrorType"]?.GetValue<string>() == nameof(PikaFollowRejectedException)) &&
                f.Rpc.Requests.Count(r => r.Route == "/api/pika/follow-start") == 1 && f.Mode == "pause",
                "Diagnostics lost per-channel timing, restarted following or defeated final holding");
        }
        await using (var disconnected = new FollowFixture())
        {
            disconnected.ConfigureGroup(); disconnected.UseUiDispatcher(); await disconnected.Click("group.follow.toggle");
            await Until(() => disconnected.GroupArmedForPlay, "Disconnected-stream fixture did not arm");
            disconnected.Rpc.StreamUnavailable = true; await disconnected.Gap(disconnected.Plugin.ReadTelemetry);
            await Until(() => !disconnected.GroupActive, "A global telemetry disconnect retained grouped authority");
            Check(disconnected.Mode == "edit" && disconnected.Rpc.Requests.IsEmpty,
                "A disconnected Edit preparation sent hardware targets or entered Play");
        }
        foreach (string broken in new[] { "devices", "pikas" })
        {
            await using var f = new FollowFixture(); f.ConfigureGroup(); f.UseUiDispatcher();
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Malformed-inventory fixture did not arm");
            f.RequestGroupPlay(); await Until(() => f.GroupPhase == GroupFollowPhase.Following, "Malformed-inventory fixture did not start");
            f.Edit(snapshot => snapshot[broken]!.AsArray().Add(snapshot[broken]![0]!.DeepClone()));
            await Until(() => !f.GroupActive, "An ambiguous lane prevented whole-session retirement");
            Check(f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold" && r.Body["device_id"]?.GetValue<string>() == "can-b") &&
                f.Rpc.Requests.Any(r => r.Route == "/api/pika/hold" && r.Body["pika_device"]?.GetValue<string>() == "/dev/pika-b"),
                "One lane's ambiguous evidence suppressed its healthy partner's independent arm/Pika holds: " + broken);
            Check(f.EngineRequests.Any(r => r.Action == "cancel-start") && f.EngineRequests.Any(r => r.Action == "pause") && f.Mode == "pause",
                "An evidence lookup failure skipped engine cancel/pause: " + broken);
            string first = broken == "devices" ? "First: arm hold unverified, gripper hold acknowledged" : "First: arm hold acknowledged, gripper hold unverified";
            Check(f.SidebarStatus().Contains(first) && f.SidebarStatus().Contains("Second: arm hold acknowledged, gripper hold acknowledged"),
                "The retirement result conflated a failed channel with its acknowledged partner holds: " + broken);
            Check(!f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() is "reset" or "disable"),
                "Malformed release evidence reset or disabled another participant");
        }
        await using (var discovery = new FollowFixture())
        {
            discovery.ConfigureGroup(); discovery.SelectGroupFollowers("piper-2"); discovery.UseUiDispatcher(); discovery.LinkEditorDelayMilliseconds = 180;
            await discovery.Click("group.follow.toggle");
            await Until(() => discovery.EngineRequests.Any(r => r.Method == "scene.robots"), "Read-only discovery did not start");
            await discovery.Click("stop"); await Task.Delay(250);
            Check(!discovery.GroupActive && !discovery.EngineRequests.Any(r => r.Action == "arm-next-play") &&
                !discovery.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() is "pose" or "follow-start" or "follow-update"),
                "STOP during discovery later armed the unchecked/view-independent participant");
        }
        foreach (var (action, backendChanged) in new[] { ("arm-next-play", false), ("activate-start", false), ("activate-start", true) })
        {
            await using var f = new FollowFixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.DelayedRuntimeAction = action;
            await f.Click("group.follow.toggle");
            if (action == "activate-start") { await Until(() => f.GroupArmedForPlay, "Delayed activation did not arm"); f.RequestGroupPlay(); }
            await f.RuntimeReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var original = f.Plugin.follow.Session!;
            if (backendChanged) f.Plugin.telemetry.AdoptBackend("replacement-backend");
            await f.ClickSidebarGroupStop().WaitAsync(TimeSpan.FromSeconds(1));
            Check(f.GroupPhase == GroupFollowPhase.Retiring && ReferenceEquals(original, f.Session),
                "Stop blocked the dispatcher or a pending editor ACK released the preparation identity before retiring: " + action);
            f.ReleaseRuntime.TrySetResult();
            await Until(() => !f.GroupActive, "A late editor ACK failed to finish cleanup: " + action);
            Check(f.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 && f.EngineRequests.Any(r => r.Action == "cancel-start") &&
                f.Mode == (action == "arm-next-play" ? "edit" : "pause"),
                "A late editor ACK revived Play, admitted a replacement or escaped final cancellation: " + action);
            Check(action != "arm-next-play" || f.Rpc.Requests.IsEmpty,
                "Cancelled Edit arming sent hardware targets after its delayed ACK");
            string expected = action == "arm-next-play" ? "First: arm untouched, gripper untouched" :
                backendChanged ? "First: arm hold unverified, gripper hold unverified" : "First: arm hold acknowledged, gripper hold acknowledged";
            Check(f.SidebarStatus().Contains(expected) && (!backendChanged || !f.Rpc.Requests.Any(r =>
                r.Body["action"]?.GetValue<string>() == "hold" || r.Route == "/api/pika/hold")),
                "The sidebar lost a release outcome or a replacement backend received the prior session's hold: " + action);
        }
        foreach (bool publish in new[] { true, false })
        {
            await using var f = new FollowFixture(); f.ConfigureGroup(); f.SelectGroupFollowers("piper-1"); f.UseUiDispatcher();
            f.DelayNativeAdmissionTelemetry = true;
            await f.Click("group.follow.toggle"); await Until(() => f.GroupArmedForPlay, "Native-reference fixture did not arm");
            f.RequestGroupPlay(); await Until(() => f.GroupPhase == GroupFollowPhase.Admitting && f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "follow-update"),
                "Native-reference admission was not primed at the frozen first frame");
            await Task.Delay(40);
            Check(f.GroupActive && f.Mode == "pause" && !f.EngineRequests.Any(r => r.Action == "activate-start"),
                "A successful native start ACK activated Play using cached pre-admission telemetry");
            if (publish)
            {
                f.PublishNativeAdmissionReference(); await Until(() => f.GroupPhase == GroupFollowPhase.Following || !f.GroupActive, "Fresh native-reference evidence did not release admission");
                Check(f.GroupActive && f.EngineRequests.Count(r => r.Action == "activate-start") == 1,
                    "An exact fresh native reference failed to release the existing admission once");
            }
            else
            {
                await Until(() => !f.GroupActive, "Missing native-reference evidence retained admission indefinitely");
                Check(f.Mode == "pause" && !f.EngineRequests.Any(r => r.Action == "activate-start") && f.Plugin.uiActionError.Contains("fresh admitted reference"),
                    "A native-reference deadline resumed playback or concealed the exact admission failure");
            }
            Check(f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 1,
                "Native-reference waiting retried an acknowledged motion admission");
        }
    }
}

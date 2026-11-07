using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Plugin;

internal static partial class SimulationFollowingTests
{
    public static async Task RunGroupFollow(Action<bool, string> check)
    {
        static async Task WaitFor(Func<bool> condition, string description)
        {
            long began = Stopwatch.GetTimestamp();
            while (!condition())
            { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 3) throw new InvalidOperationException(description); await Task.Delay(10); }
        }
        foreach (var (profile, name, transport) in new[] { ("piper-1", "First", SimulationTrajectoryTransport.StreamedPosition),
            ("piper-2", "Second", SimulationTrajectoryTransport.StreamedPosition), ("piper-2", "Second", "unknown-wire-mode") })
        {
            await using var unsupported = new Fixture(); unsupported.ConfigureGroup(); unsupported.UseUiDispatcher();
            unsupported.SetProfileTransport(profile, transport);
            // A backend capability advertisement cannot qualify a transport
            // that this coordinator deliberately does not support.
            unsupported.Snapshot["streamed_position_transport_supported"] = true;
            unsupported.Snapshot["streamed_position_wire_validation_supported"] = true;
            foreach (var device in unsupported.Snapshot["devices"]!.AsArray()) device!["streamed_position_supported"] = true;
            unsupported.Refresh();
            check(unsupported.Preparation().Issues.Any(i => i.ProfileId == profile && i.Code == "transport") &&
                unsupported.SidebarStatus().Contains(name + ": Select position-velocity"),
                "A selected robot's saved unsupported transport was silently ignored");
            await unsupported.Click("group.follow.toggle");
            await WaitFor(() => unsupported.Get<string>("groupFollowFailure").Length > 0, "Unsupported transport click did not finish");
            check(!unsupported.GroupActive && unsupported.SidebarStatus().Contains(name + ": Select position-velocity") &&
                unsupported.Rpc.Requests.IsEmpty && !unsupported.EngineRequests.Any(r => r.Action == "arm-next-play"),
                "Unsupported transport armed the engine, enabled experimental control or emitted a target");
            unsupported.SetProfileTransport(profile, SimulationTrajectoryTransport.PositionVelocity);
            await unsupported.Click("group.follow.toggle"); await WaitFor(() => unsupported.GroupArmedForPlay, "Explicit PV correction did not arm");
            check(unsupported.Mode == "edit" && unsupported.Rpc.Requests.IsEmpty && unsupported.EngineRequests.Count(r => r.Action == "arm-next-play") == 1,
                "Restoring PV silently entered Play, sent a target or armed more than once");
        }
        foreach (string saved in new[] { "valid", "missing", "malformed", "other-profile", "other-device", "other-distribution" })
        {
            await using var reference = new Fixture(); reference.ConfigureGroup(); reference.SelectGroupFollowers("piper-2"); reference.UseUiDispatcher();
            string distribution = OperatingSystem.IsWindows() ? "Ubuntu" : "";
            var value = new PikaClosedDisplayReference(1, saved == "other-profile" ? "piper-1" : "piper-2",
                saved == "other-device" ? "/dev/other" : "/dev/pika-b", saved == "other-distribution" ? "other" : distribution, -.0354, DateTimeOffset.UtcNow);
            string path = Path.Combine(reference.Get<PluginContext>("engine").Start.SettingsDirectory, "gripper-reference", "piper-2.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (saved != "missing") await File.WriteAllTextAsync(path, saved == "malformed" ? "not-json" : JsonSerializer.Serialize(value, PluginProtocol.Json));
            await reference.Click("group.follow.toggle"); await WaitFor(() => reference.GroupArmedForPlay, "Saved-reference fixture did not arm: " + saved);
            object lane = ((Array)reference.Get<object>("groupSimulation").GetType().GetField("Lanes")!.GetValue(reference.Get<object>("groupSimulation"))!).GetValue(0)!;
            var captured = (double?)lane.GetType().GetField("ClosedReference")!.GetValue(lane);
            check(captured == (saved == "valid" ? -.0354 : null) && reference.Rpc.Requests.IsEmpty,
                "Inlined display-reference load changed missing/invalid fallback or exact profile/device/distribution matching: " + saved);
            await File.WriteAllTextAsync(path, "not-json");
            check((double?)lane.GetType().GetField("ClosedReference")!.GetValue(lane) == captured,
                "An armed lane reread its captured display reference");
        }
        foreach (string scenario in new[] { "missing cache", "stale cache", "invalid current scene" })
        {
            await using var refresh = new Fixture(); refresh.ConfigureGroup(); refresh.UseUiDispatcher();
            var cached = refresh.Get<JsonObject>("scene");
            var current = cached.DeepClone().AsObject();
            cached["robots"]![0]!["joints"]!.AsArray().RemoveAt(8);
            if (scenario == "invalid current scene") current["robots"]![0]!["modelRevision"] = "";
            refresh.SetGroupSceneEvidence(scenario == "missing cache" ? null : cached, current);
            check(!refresh.Preparation().Ready, "The cached scene did not exercise a refreshable blocker: " + scenario);
            await refresh.Click("group.follow.toggle");
            await WaitFor(() => refresh.GroupPhase == GroupFollowPhase.Armed || refresh.Get<string>("groupFollowFailure").Length > 0,
                "Follow did not finish checking current scene evidence: " + scenario);
            check(refresh.EngineRequests.Any(r => r.Method == "scene.robots") && refresh.Rpc.Requests.IsEmpty && refresh.Mode == "edit",
                "Scene refresh was skipped or sent a hardware command: " + scenario);
            if (scenario == "invalid current scene")
                check(!refresh.GroupActive && !refresh.EngineRequests.Any(r => r.Action == "arm-next-play") && refresh.SidebarStatus().Contains("model identity"),
                    "Invalid current scene evidence passed the full arming gate");
            else
                check(refresh.GroupActive && refresh.EngineRequests.Count(r => r.Action == "arm-next-play") == 1,
                    "A valid current scene did not arm after refresh: " + scenario);
        }
        foreach (string robotId in new[] { "", "robot-a" })
        {
            await using var assignment = new Fixture(); assignment.ConfigureGroup(); assignment.UseUiDispatcher();
            assignment.SetSecondSceneBinding(robotId, "");
            assignment.SetGroupSceneEvidence(null);
            await assignment.Click("group.follow.toggle");
            await WaitFor(() => assignment.Get<string>("groupFollowFailure").Length > 0, "The static assignment failure did not finish");
            string reason = robotId.Length == 0 ? "Assign this robot's simulation arm" : "Assign a different simulation arm";
            check(assignment.SidebarStatus().Contains("Second: " + reason) && !assignment.GroupActive && assignment.Rpc.Requests.IsEmpty &&
                !assignment.EngineRequests.Any(r => r.Method == "scene.robots" || r.Action == "arm-next-play"),
                "A static scene assignment error was deferred to discovery");
        }
        // Exercise the same pure report used by admission and sidebar patches,
        // including a nonselected participant rather than only the visible arm.
        foreach (var (code, change) in new (string, Action<Fixture>)[] {
            ("approval", f => f.Snapshot["devices"]![1]!["generation"] = 9L),
            ("teaching", f => { f.Snapshot["devices"]![1]!["status"]!["control"] = 2; f.Snapshot["devices"]![1]!["status"]!["teach_state"] = 1; }),
            ("teaching-return", f => { f.Snapshot["devices"]![1]!["status"]!["control"] = 2; f.Snapshot["devices"]![1]!["status"]!["teach_state"] = 2; }),
            ("recovery", f => f.Snapshot["devices"]![1]!["recovery_required"] = true),
            ("arm-power", f => f.Snapshot["devices"]![1]!["joints"]![5]!["enabled"] = false),
            ("driver-fault", f => f.Snapshot["devices"]![1]!["joints"]![5]!["fault"] = 1),
            ("configuration", f => f.Snapshot["devices"]![1]!["joints"]![5]!["acceleration_fresh"] = false),
            ("configuration", f => f.Snapshot["devices"]![1]!["joints"]![5]!["max_speed_rad_s"] = 0d),
            ("joint-range", f => f.Snapshot["devices"]![1]!["joints"]![5]!["actual_deg"] = 181d),
            ("arm-fault", f => f.Snapshot["devices"]![1]!["status"]!["error"] = 1),
            ("arm-feedback", f => f.Snapshot["devices"]![1]!["status"]!["age_s"] = 1d),
            ("motion-active", f => f.Snapshot["devices"]![1]!["motion"]!["active"] = true),
            ("pika-feedback", f => f.Snapshot["pikas"]![1]!["age_s"] = 1d),
            ("pika-owner", f => f.Snapshot["pikas"]![1]!["owner"] = "another-owner"),
            ("pika-power", f => f.Snapshot["pikas"]![1]!["enabled"] = false),
            ("pika-power", f => f.Snapshot["pikas"]![1]!["armed"] = false),
            ("pika-fault", f => f.Snapshot["pikas"]![1]!["driver_fault"] = true),
            ("scene-binding", f => f.Get<JsonObject>("scene")["robots"]![0]!["joints"]!.AsArray().RemoveAt(8)),
        })
        {
            await using var invalid = new Fixture(); invalid.ConfigureGroup(); change(invalid); invalid.Refresh();
            var report = invalid.Preparation();
            check(!report.Ready && report.Issues.Any(i => i.ProfileId == "piper-2" && i.RobotName == "Second" && i.Code == code),
                "Group preparation hid a nonselected robot's named " + code + " blocker");
        }
        await using (var blocked = new Fixture())
        {
            blocked.ConfigureGroup(); blocked.UseUiDispatcher();
            var arm = blocked.Snapshot["devices"]![0]!;
            arm["status"]!["control"] = 2; arm["status"]!["teach_state"] = 2;
            blocked.Snapshot["pikas"]![0]!["enabled"] = false;
            blocked.Refresh();
            check(blocked.SidebarStatus().Contains("First: Use Resume controls") && blocked.SidebarStatus().Contains("Pika gripper"),
                "The sidebar claimed the selected group was ready while its first arm and Pika were not");
            await blocked.Click("group.follow.toggle");
            await WaitFor(() => blocked.Get<string>("groupFollowFailure").Length > 0, "The public Follow event did not publish its setup failure");
            check(!blocked.GroupActive && blocked.SidebarStatus().Contains("Follow not armed") && blocked.SidebarStatus().Contains("First: Use Resume controls") &&
                blocked.SidebarStatus().Contains("Pika gripper") && blocked.Rpc.Requests.IsEmpty && !blocked.EngineRequests.Any(r => r.Action == "arm-next-play"),
                "A public Follow click hid setup failure, armed an unready group or emitted a hardware command");
            arm["status"]!["control"] = 1; arm["status"]!["teach_state"] = 0; blocked.Snapshot["pikas"]![0]!["enabled"] = true; blocked.Refresh();
            check(blocked.SidebarStatus().Contains("Follow not armed"), "Telemetry erased the retained setup error before an explicit retry");
            await blocked.Click("group.follow.toggle");
            await WaitFor(() => blocked.GroupPhase == GroupFollowPhase.Armed || blocked.EngineRequests.Any(r => r.Action == "arm-next-play"), "The ready retry never reached the engine arm latch");
            check(blocked.GroupActive && blocked.Mode == "edit" && blocked.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 && blocked.Rpc.Requests.IsEmpty,
                "Retrying a ready group failed to arm exactly once in Edit or retained a superseded setup failure");
        }
        await using (var stopped = new Fixture())
        {
            stopped.ConfigureGroup(); stopped.UseUiDispatcher();
            stopped.SetGroupSceneEvidence(null);
            stopped.Snapshot["devices"]![1]!["status"]!["arm_status"] = 1;
            stopped.Snapshot["devices"]![1]!["recovery_required"] = true; stopped.Refresh();
            await stopped.Click("group.follow.toggle");
            await WaitFor(() => stopped.Get<string>("groupFollowFailure").Length > 0, "The stopped partner's public Follow event did not finish");
            check(stopped.SidebarStatus().Contains("Second: Open recovery") && stopped.Rpc.Requests.IsEmpty &&
                stopped.Snapshot["devices"]![1]!["recovery_required"]!.GetValue<bool>() &&
                !stopped.EngineRequests.Any(r => r.Method == "scene.robots" || r.Action == "arm-next-play"),
                "Follow admission cleared a recorded STOP or concealed the stopped partner");
        }
        await using (var idle = new Fixture())
        {
            idle.ConfigureGroup();
            idle.Snapshot["devices"]![0]!["status"]!["stationary_s"] = 0d;
            idle.Snapshot["devices"]![0]!["teaching"] = new JsonObject { ["can_return"] = false };
            idle.Refresh();
            check(idle.Preparation().Ready && idle.SidebarFollowEnabled(), "Transient encoder stillness disabled the explicit global Follow action");
        }
        await using (var armed = new Fixture())
        {
            armed.ConfigureGroup(); armed.UseUiDispatcher();
            await armed.GroupEvent("toggle");
            await armed.Commands.WaitAsync();
            try
            {
                await armed.ClickSidebarGroupStop().WaitAsync(TimeSpan.FromSeconds(2));
                await WaitFor(() => !armed.GroupActive, "An armed group did not retire outside the command gate");
                check(!armed.GroupActive && armed.Commands.CurrentCount == 0 && armed.Mode == "edit",
                    "The visible sidebar Stop waited for the command semaphore instead of retiring an armed group immediately");
                check(armed.Rpc.Requests.IsEmpty && armed.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 &&
                    armed.EngineRequests.Any(r => r.Action == "cancel-start") &&
                    !armed.EngineRequests.Any(r => r.Action is "prepare-start" or "activate-start" or "mark-start-ready" or "pause"),
                    "Cancelling an armed group entered Play, prepared a runtime or emitted a physical target");
            }
            finally { armed.Commands.Release(); }
        }
        await using var f = new Fixture(); f.ConfigureGroup(); f.UseUiDispatcher();
        await f.Click("group.follow.toggle");
        await WaitFor(() => f.GroupActive && f.EngineRequests.Any(r => r.Action == "arm-next-play"), "The public Follow event never armed its ready group");
        check(f.GroupActive && f.Mode == "edit" && f.Rpc.Requests.IsEmpty &&
            f.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 && !f.EngineRequests.Any(r => r.Action == "prepare-start"),
            "The global arm button entered Play or moved hardware before the user pressed Play");
        f.RequestGroupPlay();
        async Task Until(Func<bool> test, string why)
        {
            long began = Stopwatch.GetTimestamp();
            while (!test()) { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 8) throw new InvalidOperationException(why + " / " + f.Get<string>("GroupFollowStatus")); await Task.Delay(10); }
        }
        await Until(() => f.GroupPhase == GroupFollowPhase.Following || !f.GroupActive, "The grouped preparation never activated");
        check(f.GroupActive && f.Mode == "play", "The group did not activate one shared user-requested Play run: " + f.Get<string>("GroupFollowStatus"));
        check(f.Rpc.Requests.Where(r => r.Body["action"]?.GetValue<string>() == "follow-start").Select(r => r.Body["device_id"]!.GetValue<string>()).Order().SequenceEqual(new[] { "can-a", "can-b" }),
            "Both native device lanes were not independently admitted");
        check(f.Rpc.Requests.Where(r => r.Route == "/api/pika/follow-start").Select(r => r.Body["pika_device"]!.GetValue<string>()).Order().SequenceEqual(new[] { "/dev/pika-a", "/dev/pika-b" }) &&
            f.EngineRequests.Count(r => r.Action == "activate-start") == 1 && !f.Rpc.Requests.Any(r => r.Route == "/api/select"),
            "Grouped following redirected a gripper, switched native selection or activated Play twice");
        f.LoseNextOrderedReply = true;
        await f.OrderedReplyLost.Task.WaitAsync(TimeSpan.FromSeconds(3));
        int publications = f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update");
        int reads = f.CompletedOrderedReads;
        await Task.Delay(80);
        check(f.GroupActive &&
            f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update") > publications,
            "A delayed grouped read stopped independent lane publications before the retained lease expired");
        await Until(() => f.CompletedOrderedReads > reads || !f.GroupActive, "A lost grouped reply never recovered");
        check(f.GroupActive && f.EngineRequests.Count(r => r.Method == "robot.runtime.frames" && r.Action != "start" &&
            r.Body["afterFrameCursor"]?.GetValue<string>() == f.LostReplyCursor) >= 2 &&
            f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 2,
            "A150ms grouped read retry skipped its retained cursor, restarted admissions or retired healthy lanes");
        f.Snapshot["devices"]![1]!["status"]!["pose_stationary_s"] = 0d; f.TerminalFrame = true; f.Refresh();
        await Task.Delay(250);
        check(!f.EngineRequests.Any(r => r.Action == "ack-episode"), "The shared episode advanced while the second physical arm was not settled");
        await f.Click("profiles.select.piper-2");
        await WaitFor(() => f.Get<Preferences>("preferences").ActiveRobotId == "piper-2", "The public profile-selection event was not processed");
        int before = f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update");
        await Until(() => f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update") > before + 2, "Changing the viewed profile stopped group publication");
        check(f.Rpc.Requests.Where(r => r.Body["action"]?.GetValue<string>() == "follow-update").TakeLast(8).Select(r => r.Body["device_id"]!.GetValue<string>()).Distinct().Count() == 2,
            "Changing profile focus redirected both targets to the visible arm");
        check(f.Get<Preferences>("preferences").ActiveRobotId == "piper-2" && f.GroupActive &&
            f.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 && f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 2,
            "The public profile-selection event changed group ownership or restarted either physical follower");
        f.Snapshot["devices"]![1]!["status"]!["pose_stationary_s"] = 10d; f.Refresh();
        await Until(() => f.EngineRequests.Any(r => r.Action == "ack-episode"), "All-lane arrival never released its shared episode");
        check(f.EngineRequests.Count(r => r.Action == "ack-episode") == 1, "The same whole episode was acknowledged separately per arm");
        f.Rpc.HoldFailure = true;
        f.Snapshot["devices"]![1]!["generation"] = 9L; f.Refresh();
        await Until(() => !f.GroupActive, "A replacement CAN generation did not retire all group workers");
        check(f.Mode == "pause" && f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold" && r.Body["device_id"]?.GetValue<string>() == "can-a") &&
            f.Rpc.Requests.Where(r => r.Route == "/api/pika/hold").Select(r => r.Body["pika_device"]!.GetValue<string>()).Distinct().Count() == 2,
            "One lane's failed arm hold suppressed independent partner/Pika holds or left simulation running");
        check(!f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() is "disable" or "reset" || r.Route is "/api/pika/disable" or "/api/pika/disconnect") &&
            f.Get<string>("uiActionError").Contains("CAN connection changed"), "Grouped cleanup removed power or lost the actual first generation-change failure");

        var expiryClock = new FollowTestClock();
        await using var expired = new Fixture(followClock: expiryClock); expired.ConfigureGroup();
        await expired.GroupEvent("toggle"); expired.RequestGroupPlay();
        long started = Stopwatch.GetTimestamp();
        while (expired.GroupPhase != GroupFollowPhase.Following)
        { if (!expired.GroupActive || Stopwatch.GetElapsedTime(started).TotalSeconds > 5) throw new InvalidOperationException("Group expiry fixture did not activate"); await Task.Delay(10); }
        expired.LoseNextOrderedReply = true;
        await expired.OrderedReplyLost.Task.WaitAsync(TimeSpan.FromSeconds(3));
        expired.RuntimeReadTimeout = true;
        expiryClock.Advance(TimeSpan.FromMilliseconds(250));
        expired.Refresh();
        started = Stopwatch.GetTimestamp();
        while (expired.GroupActive)
        { if (Stopwatch.GetElapsedTime(started).TotalSeconds > 3) throw new InvalidOperationException("Repeated read deadlines kept an expired grouped source alive"); await Task.Delay(10); }
        check(expired.Get<string>("uiActionError").Contains("expired after 250 ms", StringComparison.Ordinal) && expired.Mode == "pause",
            "Repeated grouped retries renewed the original source lease or hid its250ms expiry");
        check(expired.Rpc.Requests.Where(r => r.Body["action"]?.GetValue<string>() == "hold").Select(r => r.Body["device_id"]!.GetValue<string>()).Distinct().Count() == 2 &&
            expired.Rpc.Requests.Count(r => r.Route == "/api/pika/hold") == 2,
            "Grouped source expiry did not independently hold all original arm and gripper lanes");
    }
    internal static async Task RunTeachingResume(Action<bool, string> check)
    {
        await using (var f = new Fixture())
        {
            f.ConfigureTeachingResume();
            Task resume = f.PrimaryResume();
            for (int i = 0; i < 100 && !f.Get<bool>("resumeControlsBusy"); i++) { f.Refresh(); await Task.Delay(5); }
            string pending = f.Get<string>("primaryActionStatus");
            for (int i = 0; i < 5; i++) { f.Refresh(); await Task.Delay(40); }
            check(f.Get<bool>("resumeControlsBusy") && f.Get<bool>("primaryActionBusy") && f.Rpc.Requests.IsEmpty &&
                f.Get<string>("primaryActionStatus") == pending && pending.Contains("Hold the arm still", StringComparison.Ordinal),
                "A pending Resume flickered, exited early or issued a command before native stillness admission");
            var patch = f.PrimaryResumePatch();
            check(patch["text"]?.GetValue<string>() == "Resuming controls…" && patch["enabled"]?.GetValue<bool>() == false,
                "Final unified patches overrode the stable pending Resume button");
            f.SetResumeEligible();
            await resume.WaitAsync(TimeSpan.FromSeconds(2));
            var request = f.Rpc.Requests.Single();
            check(request.Route == "/api/command" && request.Body["action"]?.GetValue<string>() == "teach-return" &&
                request.Body["device_id"]?.GetValue<string>() == "can-a" && request.Body["generation"]?.GetValue<long>() == 7 &&
                request.Body["same_pose_confirmed"]?.GetValue<bool>() == true,
                "One explicit resume did not send exactly one pinned measured-pose handoff");
            check(f.Get<bool>("approved") && !f.Get<bool>("primaryActionBusy") && !f.Get<bool>("resumeControlsBusy") &&
                f.Get<string>("primaryActionStatus").StartsWith("Controls restored", StringComparison.Ordinal) && f.Mode == "edit",
                "Successful resume failed its final approval/presentation or entered Play");
        }
        await using (var f = new Fixture())
        {
            f.ConfigureTeachingResume(completeImmediately: false); f.SetResumeEligible();
            f.Rpc.DelayedOperation = "heartbeat";
            Task resume = f.PrimaryResume();
            await f.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(!resume.IsCompleted && !f.Get<bool>("approved") && f.Get<bool>("resumeControlsBusy"),
                "CAN mode alone completed Resume before the native handoff acknowledgement");
            var pendingMotion = f.Get<LatestTelemetry>("telemetry").Latest!.State["devices"]![0]!["motion"]!;
            f.CompleteResume();
            f.Rpc.ReleaseDelayed.TrySetResult();
            check(pendingMotion["active"]?.GetValue<bool>() == true && pendingMotion["phase"]?.GetValue<string>() == "teaching-return",
                "Publishing the completion mutated an earlier active telemetry frame");
            await resume.WaitAsync(TimeSpan.FromSeconds(2));
            check(f.Rpc.Requests.Count(r => r.Route == "/api/command") == 1 &&
                f.Rpc.Requests.Where(r => r.Route == "/api/heartbeat").All(r => r.Body["device_id"]?.GetValue<string>() == "can-a" &&
                    r.Body["generation"]?.GetValue<long>() == 7 && r.Body["request_id"]?.GetValue<long>() == 77),
                "Resume retried admission or sent an unscoped handoff heartbeat");
        }
        foreach (string interruption in new[] { "permission", "generation", "playback", "fault" })
        {
            await using var f = new Fixture(); f.ConfigureTeachingResume();
            Task resume = f.PrimaryResume();
            for (int i = 0; i < 100 && !f.Get<bool>("resumeControlsBusy"); i++) { f.Refresh(); await Task.Delay(5); }
            f.InterruptResume(interruption);
            bool rejected = false;
            try { await resume.WaitAsync(TimeSpan.FromSeconds(2)); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && f.Rpc.Requests.IsEmpty && !f.Get<bool>("approved") &&
                !f.Get<bool>("primaryActionBusy") && f.Get<string>("primaryActionStatus").StartsWith("Could not start:", StringComparison.Ordinal),
                "A pending resume survived revocation, identity change, playback or a fault, or hid its failure");
        }
        await using (var f = new Fixture())
        {
            f.ConfigureTeachingResume(completeImmediately: false); f.SetResumeEligible(); f.CompleteResumeOnHeartbeat();
            await f.PrimaryResume().WaitAsync(TimeSpan.FromSeconds(2));
            check(f.Get<bool>("approved") && f.Rpc.Requests.Count(r => r.Route == "/api/command") == 1 &&
                f.Rpc.Requests.Count(r => r.Route == "/api/heartbeat") == 1,
                "A final heartbeat racing native completion falsely failed or retried a successful resume");
        }
    }

    internal static async Task RunPrimaryWorkspaceAction(Action<bool, string> check)
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
            check(f.Get<RobotLink?>("link") is not null && f.Get<bool>("approved") && f.PrimaryActionName() == "Enabled",
                "Primary Enable did not leave a ready linked robot after Settings navigation");
            check(f.Session is null && !f.Ready && !f.PlayStarted && f.Mode == "edit" && !f.EngineRequests.Any(r => r.Method == "robot.runtime.control"),
                "Primary Enable silently prepared a runtime or entered Play instead of waiting for the global group action");
        }
        await using (var blocked = new Fixture())
        {
            blocked.SetSpeed(0); blocked.UseUiDispatcher();
            await blocked.Click("enable");
            var receipt = await WaitPrimary(blocked, "complete");
            check(receipt["stage"]?.GetValue<string>() == "complete" && receipt["failure"] is null &&
                !receipt.ToJsonString().Contains("fixture-backend", StringComparison.Ordinal),
                "Enable at zero selected speed did not complete its control-only lifecycle or leaked the backend token");
            check(blocked.Session is null && !blocked.EngineRequests.Any(r => r.Action == "prepare-start") && blocked.Rpc.Requests.IsEmpty,
                "A blocked primary action prepared a runtime or sent a physical request");
        }
        await using (var gripperUnready = new Fixture())
        {
            gripperUnready.SetGripperUnready(); gripperUnready.UseUiDispatcher();
            await gripperUnready.Navigate("settings.robot"); await gripperUnready.Navigate("controls");
            check(gripperUnready.Get<bool>("approved") && RobotPowerState.Inspect(gripperUnready.Snapshot["devices"]![0]!.AsObject()).AllEnabled &&
                gripperUnready.PrimaryActionName() == "Enable robot", "Unready-gripper fixture did not exercise the approved, powered arm's Enable label");
            await gripperUnready.Click("enable");
            _ = await WaitPrimary(gripperUnready, "complete");
            check(gripperUnready.Rpc.Requests.Select(r => r.Route).SequenceEqual(["/api/pika/connect", "/api/pika/enable"]) &&
                gripperUnready.Get<bool>("pikaOwned") && gripperUnready.PrimaryActionName() == "Enabled",
                "Enable robot did not finish the assigned read-only/unready gripper connection");
            check(gripperUnready.Session is null && !gripperUnready.Ready && !gripperUnready.PlayStarted && gripperUnready.Mode == "edit" &&
                !gripperUnready.EngineRequests.Any(r => r.Method == "robot.runtime.control"),
                "An Enable robot click silently toggled simulation following or started first-pose matching after gripper enable");
        }
        foreach (bool failConfirmation in new[] { false, true })
        {
            await using var confirmed = new Fixture();
            confirmed.SetApproved(false); confirmed.UseUiDispatcher();
            await confirmed.Click("enable");
            var initial = await WaitPrimary(confirmed, "complete");
            check(!confirmed.Get<bool>("approved") && confirmed.Session is null && confirmed.Rpc.Requests.IsEmpty,
                "An unconfirmed Enable click acquired robot control or prepared following");
            if (failConfirmation)
            {
                confirmed.Snapshot["devices"]![0]!["status"]!["control"] = 2;
                confirmed.Snapshot["devices"]![0]!["status"]!["teach_state"] = 1;
            }
            await confirmed.Click("safety.accept");
            var result = await WaitPrimary(confirmed, failConfirmation ? "failed" : "complete", initial["id"]!.GetValue<string>());
            check(!confirmed.Get<bool>("primaryActionBusy") && (failConfirmation
                ? !confirmed.Get<bool>("approved") && confirmed.Get<string>("primaryActionStatus").StartsWith("Could not start:", StringComparison.Ordinal) && result["failure"]!.GetValue<string>().Contains("hand guiding", StringComparison.OrdinalIgnoreCase)
                : confirmed.Get<string>("primaryActionStatus").Length == 0 && result["failure"] is null && confirmed.Get<string>("message").StartsWith("Ready ·", StringComparison.Ordinal)),
                "Safety confirmation did not complete its shared progress/failure lifecycle");
            check(confirmed.Session is null && !confirmed.EngineRequests.Any(r => r.Action == "prepare-start") && confirmed.Rpc.Requests.IsEmpty,
                "Safety confirmation of an already enabled arm silently started following or sent a physical request");
        }

        static async Task<JsonObject> WaitPrimary(Fixture fixture, string stage, string? previousId = null)
        {
            long began = Stopwatch.GetTimestamp();
            while (true)
            {
                if (!fixture.Get<bool>("primaryActionBusy") && File.Exists(fixture.PrimaryReceipt))
                {
                    var receipt = JsonNode.Parse(await File.ReadAllTextAsync(fixture.PrimaryReceipt))!.AsObject();
                    if (receipt["stage"]?.GetValue<string>() == stage && receipt["id"]?.GetValue<string>() != previousId) return receipt;
                }
                if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5)
                    throw new InvalidOperationException("Primary confirmation did not finish as " + stage + ": " + fixture.Get<string>("message"));
                await Task.Delay(10);
            }
        }
    }
    private sealed class FollowTestClock : TimeProvider
    {
        private long offset, frozenAt;
        public override long GetTimestamp()
        {
            long frozen = Volatile.Read(ref frozenAt);
            return (frozen == 0 ? Stopwatch.GetTimestamp() : frozen) + Interlocked.Read(ref offset);
        }
        public void Freeze() => Volatile.Write(ref frozenAt, Stopwatch.GetTimestamp());
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref offset, (long)(elapsed.TotalSeconds * TimestampFrequency));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly PipersPlugin plugin;
        private readonly TimeProvider followClock;
        private readonly object resumeFrameGate = new();
        private bool publishResumeFrameCopies;
        public readonly FakeCalls Rpc = new();
        public ConcurrentQueue<(string Method, string Action, JsonObject Body)> EngineRequests { get; } = new();
        public ConcurrentQueue<(string Method, string Action, JsonObject Body)> AppliedEngineRequests { get; } = new();
        public ConcurrentQueue<string> EngineReports { get; } = new();
        private readonly string settingsDirectory = Path.Combine(Path.GetTempPath(), "piper-follow-fixture-" + Guid.NewGuid().ToString("N"));
        public JsonObject Snapshot { get; }
        public volatile string Mode = "edit";
        public volatile string? TimeMode = "deterministic-realtime";
        public volatile string RunId = "run-a", DelayedRuntimeAction = "";
        public volatile bool InvalidModel, UnsettledStart;
        public volatile bool RuntimeReadTimeout;
        public volatile bool DelayNativeAdmissionTelemetry;
        private long pendingNativeSequence;
        private double[] pendingNativeCommand = new double[6];
        public volatile bool LoseNextOrderedReply;
        public int CompletedOrderedReads;
        public string LostReplyCursor = "";
        public TaskCompletionSource OrderedReplyLost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool ChangeActivationFrame;
        public bool WrongPlayPreparation;
        public bool FreezeClockAtActivation;
        public bool PreparationSupported = true, ActivationSupported = true, AutoPrepare = true, FramesSupported = true;
        public volatile bool FrameCursorGap, TerminalFrame;
        public bool ScriptedMissedGrasp, ScriptedSourceMiss;
        public bool GroupMode;
        private JsonObject? currentSceneEvidence;
        private string[] capturedGroupTracks = [];
        private bool groupArmed;
        private readonly CancellationTokenSource groupTelemetryStop = new();
        private Task? groupTelemetryWorker;
        public string EpisodeId = "";
        public double FrameSourceAgeMs;
        public ulong SealedFrameCursor;
        private double pikaRequestAt;
        private volatile bool readyGate;
        private string prepareId = "";
        private readonly ConcurrentDictionary<string, byte> canceledPreparations = new();
        private double simulationTime;
        private ulong frameCursor;
        private readonly List<JsonObject> sourceFrames = [];
        public bool Ready => readyGate;
        private volatile bool startRequested, activated;
        public bool PlayStarted => GroupPhase == GroupFollowPhase.Following;
        public bool TryUserPlay() { if (!readyGate || Mode != "pause") return false; startRequested = true; return true; }
        public TaskCompletionSource RuntimeReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRuntime { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private double scale = 1;
        public double Scale { get => Volatile.Read(ref scale); set => Volatile.Write(ref scale, value); }
        private long sequence;
        public double EnvelopeAgeMs;
        public bool FreezeTelemetry;
        public int LinkEditorDelayMilliseconds;
        public string PrimaryReceipt => Path.Combine(settingsDirectory, "diagnostics", "primary-action", "latest.json");
        public object? Session => Get<object?>("groupSimulation");
        public bool GroupActive => Get<object?>("groupSimulation") is not null;
        public GroupFollowPhase? GroupPhase => Session is { } group ? (GroupFollowPhase)group.GetType().GetProperty("Phase")!.GetValue(group)! : null;
        public async Task GroupEvent(string action)
        { await (Task)typeof(PipersPlugin).GetMethod("GroupFollowEvent", Fields)!.Invoke(plugin, [action, CancellationToken.None])!; }
        public FollowPreparationReport Preparation() => (FollowPreparationReport)typeof(PipersPlugin).GetMethod("GroupPreparation", Fields)!
            .Invoke(plugin, [null, null, null])!;
        public string SidebarStatus() => Sidebar().Children!.Single(w => w.Id == "group.follow.status").Text!;
        public bool SidebarFollowEnabled() => Sidebar().Children!.Single(w => w.Id == "group.follow.toggle").Enabled != false;
        private Widget Sidebar() => (Widget)typeof(PipersPlugin).GetMethod("GroupFollowControls", Fields)!.Invoke(plugin, null)!;
        public Task ClickSidebarGroupStop()
        {
            var sidebar = (Widget)typeof(PipersPlugin).GetMethod("GroupFollowControls", Fields)!.Invoke(plugin, null)!;
            var stop = sidebar.Children!.Single(w => w.Text == "Stop following" && w.Visible != false);
            string wire = WorkspaceEventScope.Wrap(Get<long>("workspaceViewEpoch"), "piper-1", stop.Id);
            return plugin.HandleEventAsync(new("fixture", wire, "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
        }
        public void RequestGroupPlay() { if (!groupArmed || Mode != "edit") throw new InvalidOperationException("Group is not armed"); startRequested = true; Mode = "pause"; }
        public bool GroupArmedForPlay => groupArmed && GroupActive;
        public void SelectGroupFollowers(params string[] ids)
        {
            var current = Get<Preferences>("preferences");
            Set("preferences", current with { Robots = current.Robots!.Select(p => p with { FollowSimulationSelected = ids.Contains(p.Id) }).ToArray() });
            Refresh();
        }
        public void SetGroupSceneEvidence(JsonObject? cached, JsonObject? current = null)
        {
            Set("scene", cached);
            currentSceneEvidence = current;
        }
        public void SetSecondSceneBinding(string robotId, string prefix)
        {
            var current = Get<Preferences>("preferences");
            Set("preferences", current with { Robots = current.Robots!.Select(p => p.Id == "piper-2" ? p with { RobotId = robotId, RobotJointPrefix = prefix } : p).ToArray() });
            var authority = plugin.CommandAuthority;
            authority.TrySaveApproval(authority.Epoch, "piper-2",
                authority.Current.Approvals["piper-2"] with { SceneRobot = robotId, JointPrefix = prefix });
        }
        public void FocusGroupSecond()
        { Set("preferences", RobotProfileRouting.Activate(Get<Preferences>("preferences"), "piper-2")); Snapshot["selected"] = "can-b"; Refresh(); }
        public void ConfigureGroup()
        {
            GroupMode = true; EpisodeId = "episode-group";
            var profiles = new[] { new RobotProfile("piper-1", "First", "can-a", Speed: 10, RobotId: "robot-a", PikaDevice: "/dev/pika-a", FollowSimulationSelected: true),
                new RobotProfile("piper-2", "Second", "can-b", Speed: 10, RobotId: "robot-a", PikaDevice: "/dev/pika-b", RobotJointPrefix: "piper3_", FollowSimulationSelected: true) };
            Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots: profiles, ActiveRobotId: "piper-1")));
            Get<JsonObject>("scene")["robots"]![0]!["joints"] = GroupPose()["joints"]!.DeepClone();
            Snapshot["multi_device_lanes_supported"] = true;
            var second = (JsonObject)Snapshot["devices"]![0]!.DeepClone(); second["id"] = "can-b"; second["generation"] = 8L;
            Snapshot["devices"]!.AsArray().Add(second);
            for (int i = 0; i < 2; i++)
            {
                var device = Snapshot["devices"]![i]!; device["status"]!["pose_stationary_s"] = 10d;
                var motion = (JsonObject)Snapshot["motion"]!.DeepClone(); motion["device_id"] = profiles[i].CanDeviceId; motion["generation"] = i == 0 ? 7L : 8L;
                device["motion"] = motion;
            }
            var firstPika = (JsonObject)Snapshot["pika"]!.DeepClone(); var secondPika = (JsonObject)firstPika.DeepClone(); secondPika["device"] = "/dev/pika-b";
            Snapshot["pikas"] = new JsonArray(firstPika, secondPika);
            for (int i = 0; i < profiles.Length; i++)
            {
                var p = profiles[i];
                plugin.CommandAuthority.TrySaveApproval(plugin.CommandAuthority.Epoch, p.Id,
                    new("fixture-backend", p.CanDeviceId, i == 0 ? 7L : 8L, p.RobotId, p.RobotJointPrefix, p.PikaDevice, p.WslDistribution));
            }
            Rpc.Accepted = (route, body) =>
            {
                var device = Snapshot["devices"]!.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == body["device_id"]?.GetValue<string>());
                string action = body["action"]?.GetValue<string>() ?? "";
                if (device is not null && route == "/api/command")
                {
                    var motion = device["motion"]!;
                    if (action == "follow-start" && !DelayNativeAdmissionTelemetry) { motion["active"] = true; motion["following"] = true; motion["trajectory_follow"] = true; motion["request_id"] = 77L; motion["follow_sequence"] = 0L; }
                    if (action == "follow-update")
                    {
                        pendingNativeSequence = body["sequence"]!.GetValue<long>(); pendingNativeCommand = body["degrees"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
                        if (!DelayNativeAdmissionTelemetry)
                        {
                            motion["follow_sequence"] = body["sequence"]!.DeepClone();
                            for (int i = 0; i < 6; i++) { device["joints"]![i]!["command_deg"] = body["degrees"]![i]!.DeepClone(); device["joints"]![i]!["actual_deg"] = body["degrees"]![i]!.DeepClone(); }
                        }
                    }
                    if (action == "hold") { motion["active"] = false; motion["following"] = false; }
                }
                var pika = Snapshot["pikas"]!.AsArray().OfType<JsonObject>().SingleOrDefault(p => p["device"]?.GetValue<string>() == body["pika_device"]?.GetValue<string>());
                if (pika is not null && route == "/api/pika/follow-update") pika["telemetry"]!["motor"]!["Position"] = PikaProtocol.Angle(body["opening_mm"]!.GetValue<double>());
                if (pika is not null && route == "/api/pika/position") pika["telemetry"]!["motor"]!["Position"] = body["value"]!.DeepClone();
                Refresh();
            };
            Refresh();
            groupTelemetryWorker = Task.Run(async () =>
            { while (!groupTelemetryStop.IsCancellationRequested) { Refresh(); await Task.Delay(10); } });
        }

        private JsonObject GroupPose(double time = 0, long poseSequence = 1)
        {
            var pose = SimulationFollowTests.Pose(time, poseSequence);
            if (GroupMode)
            {
                foreach (var joint in pose["joints"]!.AsArray().ToArray()) { var copy = (JsonObject)joint!.DeepClone(); copy["name"] = "piper3_" + copy["name"]!.GetValue<string>(); pose["joints"]!.AsArray().Add(copy); }
                foreach (var actuator in pose["actuators"]!.AsArray().ToArray()) { var copy = (JsonObject)actuator!.DeepClone(); copy["name"] = "piper3_" + copy["name"]!.GetValue<string>(); copy["targetJoint"] = "piper3_" + copy["targetJoint"]!.GetValue<string>(); pose["actuators"]!.AsArray().Add(copy); }
            }
            return pose;
        }
        public SemaphoreSlim Commands => Get<SemaphoreSlim>("commands");
        public T Get<T>(string field) => (T)typeof(PipersPlugin).GetField(field, Fields)!.GetValue(plugin)!;
        public void SetSpeed(double speed) => Set("preferences", Get<Preferences>("preferences") with { Speed = speed });
        public void SetApproved(bool value) => Set("approved", value);
        public void SetGripperUnready()
        {
            Set("pikaOwned", false);
            Snapshot["pika"]!["enabled"] = false; Snapshot["pika"]!["armed"] = false; Snapshot["pika"]!["read_only"] = true;
            Refresh();
        }
        public string PrimaryActionName() => ((RobotReadiness)typeof(PipersPlugin).GetMethod("Readiness", Fields)!.Invoke(plugin,
            [Snapshot["devices"]![0]!.AsObject()])!).Action;
        public Task PrimaryResume() => Invoke("PrimaryRobotAction", CancellationToken.None);
        public JsonObject PrimaryResumePatch()
        {
            var patches = new Dictionary<string, JsonObject> { ["teleoperate.toggle"] = new() };
            var device = Snapshot["devices"]![0]!.AsObject();
            typeof(PipersPlugin).GetMethod("AddTeachingPatches", Fields)!.Invoke(plugin, [patches, device]);
            typeof(PipersPlugin).GetMethod("AddUnifiedWorkspacePatches", Fields)!.Invoke(plugin, [patches, device]);
            return patches["enable"];
        }
        public void ConfigureTeachingResume(bool completeImmediately = true)
        {
            publishResumeFrameCopies = true;
            var device = Snapshot["devices"]![0]!;
            device["status"]!["control"] = 2; device["status"]!["teach_state"] = 2;
            device["teaching"] = new JsonObject { ["phase"] = "ended", ["return_required"] = true, ["can_return"] = false,
                ["return_reason"] = "Hold the arm still until all six motor encoders confirm stationary feedback for 0.4 seconds.",
                ["seamless_supported"] = true, ["return_requires_zero"] = false, ["return_strategy"] = "measured-pose" };
            device["motion"] = Snapshot["motion"]!.DeepClone();
            Set("approved", false); Set("teaching", TeachingState.Inspect(device.AsObject()));
            Rpc.Accepted = (route, body) =>
            {
                if (route != "/api/command" || body["action"]?.GetValue<string>() != "teach-return") return;
                lock (resumeFrameGate)
                {
                    device["motion"]!["request_id"] = 77L;
                    device["motion"]!["active"] = !completeImmediately;
                    device["motion"]!["phase"] = completeImmediately ? "complete" : "teaching-return";
                    device["status"]!["control"] = 1; device["teaching"]!["phase"] = "off";
                    device["teaching"]!["return_required"] = false;
                    Refresh();
                }
            };
            Refresh();
        }
        public void SetResumeEligible()
        {
            lock (resumeFrameGate)
            {
                Snapshot["devices"]![0]!["teaching"]!["can_return"] = true;
                Refresh();
            }
        }
        public void CompleteResume()
        {
            lock (resumeFrameGate)
            {
                var motion = Snapshot["devices"]![0]!["motion"]!;
                motion["active"] = false;
                motion["phase"] = "complete";
                Refresh();
            }
        }
        public void CompleteResumeOnHeartbeat()
        {
            var previous = Rpc.Accepted;
            Rpc.Accepted = (route, body) => { previous?.Invoke(route, body); if (route == "/api/heartbeat") CompleteResume(); };
            Rpc.RejectHeartbeatAfterAccepted = true;
        }
        public void InterruptResume(string kind)
        {
            lock (resumeFrameGate)
            {
                var device = Snapshot["devices"]![0]!;
                if (kind == "permission") plugin.CommandAuthority.Revoke();
                if (kind == "generation") device["generation"] = 8L;
                if (kind == "playback") device["teaching"]!["phase"] = "playback";
                if (kind == "fault") device["joints"]![3]!["fault"] = 1;
                Refresh();
            }
        }
        public void SetTransport(string transport) => Set("preferences", Get<Preferences>("preferences") with { SimulationTransport = transport });
        public void SetProfileTransport(string profile, string transport)
        {
            var current = RobotProfileRouting.Capture(Get<Preferences>("preferences"));
            current = current with { Robots = current.Robots!.Select(p => p.Id == profile ? p with { SimulationTransport = transport } : p).ToArray() };
            if (current.ActiveRobotId == profile) current = current with { SimulationTransport = transport };
            Set("preferences", current); Refresh();
        }
        private void Set(string field, object? value) => typeof(PipersPlugin).GetField(field, Fields)!.SetValue(plugin, value);

        public Fixture(bool mismatch = false, TimeProvider? followClock = null)
        {
            this.followClock = followClock ?? TimeProvider.System;
            plugin = new("http://127.0.0.1:8766", this.followClock);
            Set("client", new PiperControl.PiperControlClient(Rpc));
            Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots:
                [new("piper-1", "Fixture", "can-a", RobotId: "robot-a", PikaDevice: "/dev/pika-a")], ActiveRobotId: "piper-1")));
            Set("token", "fixture-backend"); Set("approved", true); Set("pikaOwned", true); Set("linkCommandsEnabled", true);
            var model = SimulationFollowTests.Pose();
            var sample = SimulationFollowSample.Read(model);
            Snapshot = new JsonObject
            {
                ["token"] = "fixture-backend", ["selected"] = "can-a", ["trajectory_follow_supported"] = true,
                ["motion"] = new JsonObject { ["active"] = false, ["request_id"] = 0L, ["owner"] = Get<string>("owner"),
                    ["following"] = false, ["trajectory_follow"] = false, ["follow_sequence"] = 0L },
                ["devices"] = new JsonArray(new JsonObject
                {
                    ["id"] = "can-a", ["name"] = "Fixture", ["generation"] = 7L, ["present"] = true, ["up"] = true, ["fresh"] = true,
                    ["firmware_supported"] = true, ["recovery_required"] = false, ["reset_pending"] = false, ["blocker"] = "",
                    ["status"] = new JsonObject { ["control"] = 1, ["teach_state"] = 0, ["arm_status"] = 0, ["error"] = 0, ["age_s"] = .001, ["stationary_s"] = 1d },
                    ["joints"] = new JsonArray(Enumerable.Range(0, 6).Select(i => (JsonNode)new JsonObject
                    {
                        ["index"] = i + 1, ["enabled"] = true, ["fresh"] = true, ["fault"] = 0, ["age_s"] = .001,
                        ["actual_deg"] = sample.MeasuredDegrees[i] + (i == 0 && mismatch ? 2 : 0),
                        ["command_deg"] = sample.MeasuredDegrees[i] + (i == 0 && mismatch ? 2 : 0),
                        ["min_deg"] = -180d, ["max_deg"] = 180d, ["max_speed_rad_s"] = 1d,
                        ["max_acceleration_rad_s2"] = 3d, ["limits_fresh"] = true, ["acceleration_fresh"] = true
                    }).ToArray())
                }),
                ["pika"] = new JsonObject
                {
                    ["configured"] = true, ["device"] = "/dev/pika-a", ["distribution"] = "Ubuntu", ["owner"] = Get<string>("owner"),
                    ["fresh"] = true, ["connected"] = true, ["armed"] = true, ["enabled"] = true, ["pending"] = false, ["age_s"] = .001,
                    ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Position"] = PikaProtocol.Angle(20), ["Speed"] = 0d } }
                }
            };
            Set("state", Snapshot); Set("telemetryScope", new TelemetryScope("fixture-backend", "can-a", 7));
            Rpc.Accepted = (route, body) =>
            {
                string action = body["action"]?.GetValue<string>() ?? "";
                if (route == "/api/command" && action == "pose")
                {
                    for (int i = 0; i < 6; i++) Snapshot["devices"]![0]!["joints"]![i]!["actual_deg"] = body["degrees"]![i]!.GetValue<double>();
                    Snapshot["motion"]!["request_id"] = 77L; Snapshot["motion"]!["active"] = false;
                }
                if (route == "/api/pika/position")
                {
                    Snapshot["pika"]!["telemetry"]!["motor"]!["Position"] = body["value"]!.GetValue<double>();
                    pikaRequestAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                }
                if (route == "/api/pika/connect")
                {
                    Snapshot["pika"]!["owner"] = Get<string>("owner"); Snapshot["pika"]!["connected"] = true; Snapshot["pika"]!["fresh"] = true;
                    Refresh();
                }
                if (route == "/api/pika/enable")
                {
                    Snapshot["pika"]!["enabled"] = true; Snapshot["pika"]!["armed"] = true; Snapshot["pika"]!["read_only"] = false;
                    Refresh();
                }
                if (route == "/api/command" && action == "follow-start")
                {
                    pendingNativeSequence = 0;
                    pendingNativeCommand = Snapshot["devices"]![0]!["joints"]!.AsArray().Select(joint => joint!["actual_deg"]!.GetValue<double>()).ToArray();
                    if (!DelayNativeAdmissionTelemetry) PublishNativeAdmissionReference();
                }
                if (route == "/api/command" && action == "follow-update")
                {
                    pendingNativeSequence = body["sequence"]!.GetValue<long>();
                    pendingNativeCommand = body["degrees"]!.AsArray().Select(joint => joint!.GetValue<double>()).ToArray();
                    if (!DelayNativeAdmissionTelemetry) PublishNativeAdmissionReference();
                    else Refresh();
                }
                if (route == "/api/command" && action == "hold") { Snapshot["motion"]!["active"] = false; Snapshot["motion"]!["following"] = false; }
                if (ScriptedMissedGrasp && route == "/api/pika/follow-update")
                    Snapshot["pika"]!["telemetry"]!["motor"]!["Position"] = PikaProtocol.Angle(body["opening_mm"]!.GetValue<double>());
            };
            Rpc.ArmActive = () => Snapshot["motion"]?["active"]?.GetValue<bool>() == true;
            Set("linkBackendToken", "fixture-backend");
            Set("link", new RobotLink("source-a", "robot-a", model["joints"]!.AsArray(), Snapshot["devices"]![0]!.AsObject()));
            Set("scene", new JsonObject { ["sceneId"] = "source-a", ["robots"] = new JsonArray(new JsonObject
                { ["entityId"] = "robot-a", ["modelAssetId"] = "asset-a", ["modelRevision"] = "model-a" }) });
            Refresh();
            var context = (PluginContext)Activator.CreateInstance(typeof(PluginContext), Fields, null,
                [new PluginStart("fixture", settingsDirectory, Path.GetTempPath()),
                    (Func<object, CancellationToken, Task>)((message, _) =>
                    {
                        var sent = JsonSerializer.SerializeToElement(message);
                        if (sent.GetProperty("type").GetString() == "log") EngineReports.Enqueue(sent.GetProperty("message").GetString()!);
                        return Task.CompletedTask;
                    }),
                    (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)(async (method, body, cancellation) =>
                    {
                        cancellation.ThrowIfCancellationRequested(); Refresh();
                        string action = body["action"]?.GetValue<string>() ?? "";
                        EngineRequests.Enqueue((method, action, (JsonObject)body.DeepClone()));
                        if (method is "scene.robots" or "robot.feedback" or "robot.subscribe" && LinkEditorDelayMilliseconds > 0)
                        { await Task.Delay(LinkEditorDelayMilliseconds, cancellation); Refresh(); }
                        if (method == "scene.robots") return JsonSerializer.SerializeToElement(currentSceneEvidence ?? new JsonObject { ["sceneId"] = "source-a", ["robots"] = new JsonArray(
                            new JsonObject { ["entityId"] = "robot-a", ["modelAssetId"] = "asset-a", ["modelRevision"] = "model-a", ["joints"] = GroupPose()["joints"]!.DeepClone() }) });
                        if (method == "robot.subscribe") return JsonSerializer.SerializeToElement(new { sceneId = "source-a", entityId = "robot-a" });
                        if (method == "host.info") return JsonSerializer.SerializeToElement(new { robotRuntimeFramesSupported = FramesSupported, robotRuntimeTracksSupported = GroupMode });
                        if (method is "robot.runtime.read" or "robot.runtime.frames" && RuntimeReadTimeout)
                            await Task.Delay(Timeout.Infinite, cancellation);
                        if (method == "robot.feedback")
                        {
                            if (Mode != "edit") throw new InvalidOperationException("Robot linking requires an open scene in Edit mode");
                            var measuredPose = SimulationFollowTests.Pose();
                            measuredPose["sceneId"] = "source-a";
                            foreach (var joint in measuredPose["joints"]!.AsArray().OfType<JsonObject>())
                                joint["position"] = body["joints"]![joint["name"]!.GetValue<string>()]!.DeepClone();
                            return JsonSerializer.SerializeToElement(new { applied = true, pose = measuredPose });
                        }
                        if (method == "robot.runtime.control")
                        {
                            string nonce = body["prepareId"]?.GetValue<string>() ?? "";
                            if (action == DelayedRuntimeAction) { RuntimeReached.TrySetResult(); await ReleaseRuntime.Task; }
                            if (action == "arm-next-play")
                            {
                                if (Mode != "edit") throw new InvalidOperationException("Arm-next-play requires Edit");
                                prepareId = nonce; groupArmed = true;
                                return JsonSerializer.SerializeToElement(new { applied = true, mode = Mode, prepareId, startArmedForNextPlay = true, runId = "" });
                            }
                            if (action is "pause" or "resume" or "set-time-scale" or "mark-start-ready" or "activate-start" &&
                                (Mode == "edit" || body["runId"]?.GetValue<string>() != RunId))
                                throw new InvalidOperationException("The exact runtime run changed");
                            if (action == "mark-start-ready")
                            {
                                if (nonce != prepareId || canceledPreparations.ContainsKey(nonce)) throw new InvalidOperationException("Preparation was canceled");
                                readyGate = true;
                            }
                            if (action == "set-time-scale" && Mode != "pause") throw new InvalidOperationException("Set time scale requires Pause");
                            if (action == "cancel-start")
                            {
                                canceledPreparations.TryAdd(nonce, 0);
                                if (body["runId"] is JsonValue expectedRun && expectedRun.GetValue<string>() != RunId)
                                    throw new InvalidOperationException("The exact preparation run changed");
                                if (nonce == prepareId) { groupArmed = readyGate = startRequested = false; if (Mode == "preparing") Mode = "edit"; else if (Mode != "edit") Mode = "pause"; }
                            }
                            if (body["timeScale"] is JsonValue requested && requested.TryGetValue<double>(out double requestedScale) &&
                                (!double.IsFinite(requestedScale) || requestedScale is < .01 or > 1))
                                throw new InvalidOperationException("Native time scale must be within .01 and 1");
                            if (body["timeScale"] is JsonValue value && value.TryGetValue<double>(out double next)) scale = next;
                            if (action == "pause") Mode = "pause";
                            if (action == "activate-start")
                            {
                                if (!readyGate || !startRequested || nonce != prepareId || canceledPreparations.ContainsKey(nonce) || Mode != "pause")
                                    throw new InvalidOperationException("The exact prepared Play request is unavailable");
                                activated = true; Mode = "play";
                            }
                            if (action == "resume") { if (!readyGate || !activated) throw new InvalidOperationException("The prepared start is not activated"); Mode = "play"; }
                            AppliedEngineRequests.Enqueue((method, action, (JsonObject)body.DeepClone()));
                            if (action != "activate-start") return JsonSerializer.SerializeToElement(new { applied = true, mode = Mode });
                        }
                        if (Mode == "play" && action != "activate-start") simulationTime += .005;
                        var pose = GroupPose(simulationTime, Interlocked.Increment(ref sequence));
                        if (ScriptedMissedGrasp)
                        {
                            foreach (var joint in pose["joints"]!.AsArray().OfType<JsonObject>().Where(j => Enumerable.Range(1, 6).Any(i => j["name"]!.GetValue<string>().EndsWith("joint" + i, StringComparison.Ordinal))))
                                joint["position"] = 0d;
                            foreach (var actuator in pose["actuators"]!.AsArray().OfType<JsonObject>().Where(a => a["name"]!.GetValue<string>() != "gripper"))
                                actuator["control"] = 0d;
                        }
                        pose["mode"] = Mode; pose["timeScale"] = scale;
                        pose["startPreparationSupported"] = PreparationSupported;
                        pose["startActivationSupported"] = ActivationSupported;
                        pose["startRequested"] = startRequested;
                        pose["startActivated"] = activated;
                        if (method == "robot.runtime.control") pose["applied"] = true;
                        pose["prepareId"] = WrongPlayPreparation && method == "robot.runtime.read" && Mode == "pause" ? "different-preparation" : prepareId;
                        pose["startReady"] = readyGate; pose["startMatchRequired"] = !readyGate;
                        pose["ready"] = Mode is "edit" or "pause" or "play";
                        pose["timeMode"] = TimeMode;
                        pose["runId"] = RunId; pose["sceneId"] = RunId;
                        pose["capturedMonotonicNs"] = ((long)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency))).ToString();
                        pose["capturedAtUnixMicroseconds"] = ((DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10).ToString();
                        if (Mode == "edit") { pose["runId"] = ""; pose["sceneId"] = "source-a"; }
                        if (InvalidModel) pose["actuators"]![0]!["positionControl"] = false;
                        if (UnsettledStart) pose["joints"]![0]!["position"] = .2;
                        if (action == "activate-start" && ChangeActivationFrame) pose["joints"]![0]!["position"] = .3;
                        pose["episodeId"] = EpisodeId; pose["episodeOrdinal"] = 0L; pose["episodeComplete"] = TerminalFrame;
                        pose["episodeSucceeded"] = TerminalFrame;
                        if (method == "robot.runtime.frames")
                        {
                            if (GroupMode && action == "start") capturedGroupTracks = body["tracks"]!.AsArray().Select(t => t!["id"]!.GetValue<string>()).ToArray();
                            var source = SimulationFollowSample.Read(pose);
                            if (SealedFrameCursor == 0)
                            {
                            if (FrameCursorGap) frameCursor++;
                            var nextFrame = new JsonObject { ["cursor"] = (++frameCursor).ToString(), ["simulationTimeNs"] = pose["simulationTimeNs"]!.DeepClone(),
                                ["mujocoTimeSeconds"] = source.SimTimeSeconds,
                                ["commandJoints"] = JsonSerializer.SerializeToNode(source.CommandDegrees.Select(q => q * Math.PI / 180)),
                                ["measuredJoints"] = JsonSerializer.SerializeToNode(source.MeasuredDegrees.Select(q => q * Math.PI / 180)),
                                ["commandOpeningMm"] = source.CommandOpeningMm, ["measuredOpeningMm"] = source.MeasuredOpeningMm,
                                ["episodeId"] = EpisodeId, ["episodeOrdinal"] = 0L, ["phase"] = simulationTime < .015 ? "begin:0" : "wait:1",
                                ["episodeComplete"] = TerminalFrame, ["episodeSucceeded"] = TerminalFrame };
                            if (GroupMode)
                            {
                                var rows = new JsonArray();
                                foreach (var profile in Get<Preferences>("preferences").Robots!.Where(p => capturedGroupTracks.Contains(p.Id)))
                                {
                                    var armSample = SimulationFollowSample.Read(pose, profile.RobotJointPrefix);
                                    rows.Add(new JsonObject { ["id"] = profile.Id, ["commandJoints"] = JsonSerializer.SerializeToNode(armSample.CommandDegrees.Select(q=>q*Math.PI/180)),
                                        ["measuredJoints"] = JsonSerializer.SerializeToNode(armSample.MeasuredDegrees.Select(q=>q*Math.PI/180)),
                                        ["commandOpeningMm"] = armSample.CommandOpeningMm, ["measuredOpeningMm"] = armSample.MeasuredOpeningMm });
                                }
                                nextFrame["tracks"] = rows;
                            }
                            if (ScriptedMissedGrasp)
                            {
                                int row = sourceFrames.Count;
                                string prefix = ScriptedSourceMiss ? "missed-grasp-" : "";
                                nextFrame["phase"] = row switch { 0 => "begin:0", 1 or 2 => "gripper:1", 3 => prefix + "wait:2",
                                    4 => prefix + "gripper:3", 5 => prefix + "home:4", _ => prefix + "finish:5" };
                                nextFrame["commandOpeningMm"] = row is 2 or 3 ? 10d : 20d;
                                nextFrame["measuredOpeningMm"] = row is 2 or 3 ? 14d : 20d;
                                nextFrame["episodeComplete"] = TerminalFrame = row >= 6;
                                nextFrame["episodeSucceeded"] = TerminalFrame && !ScriptedSourceMiss;
                                pose["episodeComplete"] = TerminalFrame;
                                pose["episodeSucceeded"] = TerminalFrame && !ScriptedSourceMiss;
                                if (nextFrame["tracks"] is JsonArray scriptedTracks)
                                    foreach (var track in scriptedTracks)
                                        foreach (string key in new[] { "commandJoints", "measuredJoints", "commandOpeningMm", "measuredOpeningMm" })
                                            track![key] = nextFrame[key]!.DeepClone();
                            }
                            sourceFrames.Add(nextFrame);
                            if (TerminalFrame) SealedFrameCursor = frameCursor;
                            }
                            ulong after = ulong.Parse(body["afterFrameCursor"]!.GetValue<string>());
                            pose["oldestCursor"] = "1"; pose["newestCursor"] = frameCursor.ToString(); pose["sourcePausedForFollower"] = false;
                            pose["frames"] = new JsonArray(sourceFrames.Where(row => ulong.Parse(row["cursor"]!.GetValue<string>()) > after)
                                .Take(body["maxFrames"]!.GetValue<int>()).Select(row => (JsonNode)row.DeepClone()).ToArray());
                            if (FrameSourceAgeMs > 0) pose["capturedAtUnixMicroseconds"] = ((DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10 - (long)(FrameSourceAgeMs * 1000)).ToString();
                        }

                        if (method == "robot.runtime.frames" && action != "start")
                        {
                            if (LoseNextOrderedReply)
                            {
                                LoseNextOrderedReply = false;
                                LostReplyCursor = body["afterFrameCursor"]!.GetValue<string>();
                                OrderedReplyLost.TrySetResult();
                                await Task.Delay(Timeout.Infinite, cancellation);
                            }
                            Interlocked.Increment(ref CompletedOrderedReads);
                        }
                        if (action == "activate-start" && FreezeClockAtActivation && followClock is FollowTestClock clock)
                            clock.Freeze();
                        return JsonSerializer.SerializeToElement(pose);
                    })], null)!;
            Set("engine", context);
        }

        public void PublishNativeAdmissionReference()
        {
            DelayNativeAdmissionTelemetry = false;
            Snapshot["motion"]!["request_id"] = 77L; Snapshot["motion"]!["active"] = true;
            Snapshot["motion"]!["following"] = true; Snapshot["motion"]!["trajectory_follow"] = true;
            Snapshot["motion"]!["follow_sequence"] = pendingNativeSequence;
            if (GroupMode)
            {
                var motion = Snapshot["devices"]![0]!["motion"]!;
                motion["request_id"] = 77L; motion["active"] = true; motion["following"] = true;
                motion["trajectory_follow"] = true; motion["follow_sequence"] = pendingNativeSequence;
            }
            for (int joint = 0; joint < 6; joint++) Snapshot["devices"]![0]!["joints"]![joint]!["command_deg"] = pendingNativeCommand[joint];
            Refresh();
        }

        public void Refresh()
        {
            if (FreezeTelemetry) return;
            if (publishResumeFrameCopies)
            {
                lock (resumeFrameGate)
                {
                    // Resume can retain a receipt across several checks.
                    var frame = Snapshot.DeepClone().AsObject();
                    Set("state", frame);
                    Publish(frame);
                }
                return;
            }
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            Snapshot["pika"]!["last_motor_request"] = "pika-position-fixture";
            Snapshot["pika"]!["request_time_s"] = pikaRequestAt;
            Snapshot["pika"]!["feedback_time_s"] = now;
            Snapshot["pika"]!["pending"] = false;
            if (GroupMode) foreach (var pika in Snapshot["pikas"]!.AsArray().OfType<JsonObject>()) pika["feedback_time_s"] = now;
            Publish(Snapshot);

            void Publish(JsonObject frame) => Get<LatestTelemetry>("telemetry").Publish(
                new(frame, "fixture-backend", Interlocked.Increment(ref sequence), followClock.GetTimestamp(), EnvelopeAgeMs));
        }
        public void RemoveMeasuredLink(int editorDelayMilliseconds) { Set("link", null); LinkEditorDelayMilliseconds = editorDelayMilliseconds; }
        public ConcurrentQueue<UiEvent> DiscardedUiEvents { get; } = new();
        public void UseUiDispatcher()
        {
            var epoch = typeof(PipersPlugin).GetProperty("ControlInputEpoch", Fields)!;
            var needsPermission = typeof(PipersPlugin).GetMethod("UiEventNeedsPermission", Fields)!;
            Set("uiEvents", new UiEventDispatch((input, cancellation) => Invoke("HandleEventCoreAsync", input, cancellation),
                () => (long)epoch.GetValue(plugin)!, input => (bool)needsPermission.Invoke(plugin, [input])!,
                error => throw new InvalidOperationException("Unexpected fixture UI actor failure", error),
                discarded: (input, _) => DiscardedUiEvents.Enqueue(input)));
        }
        public Task Navigate(string action) => Invoke("WorkspaceEvent", action, CancellationToken.None);
        public Task Click(string id)
        {
            Refresh();
            string wireId = WorkspaceEventScope.Wrap(Get<long>("workspaceViewEpoch"), "piper-1", id);
            return plugin.HandleEventAsync(new("fixture", wireId, "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
        }
        public void SetDetail(string detail) => Set("GroupFollowDetail", detail);
        public void SetWorkspace(string page, bool calibration = false) { Set("workspacePage", page); Set("calibrationOpen", calibration); }
        public Task PublishMeasured() { Refresh(); return Invoke("PublishMeasuredScene", CancellationToken.None); }
        public async Task Gap(string method)
        {
            using var duration = new CancellationTokenSource(80);
            try { await Invoke(method, duration.Token); } catch (OperationCanceledException) { }
        }
        public Task StopEvent() => plugin.HandleEventAsync(new("fixture", "robot.piper-1.simulation.stop", "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
        private Task Invoke(string method, params object?[] args) => (Task)typeof(PipersPlugin).GetMethod(method, Fields)!.Invoke(plugin, args)!;
        public async ValueTask DisposeAsync()
        {
            Rpc.ReleaseDelayed.TrySetResult();
            ReleaseRuntime.TrySetResult();
            if (GroupActive)
                await (Task)typeof(PipersPlugin).GetMethod("StopGroupSimulationFollowing", Fields)!
                    .Invoke(plugin, ["Fixture disposed", CancellationToken.None])!;
            groupTelemetryStop.Cancel(); if (groupTelemetryWorker is not null) await groupTelemetryWorker;
            if (Get<UiEventDispatch?>("uiEvents") is { } dispatch) await dispatch.DisposeAsync();
            Get<CancellationTokenSource>("shutdown").Cancel();
            ((IDisposable)Get<object>("channel")).Dispose(); Commands.Dispose();
            if (Directory.Exists(settingsDirectory)) Directory.Delete(settingsDirectory, recursive: true);
        }
    }

    private sealed class FakeCalls : CallInvoker
    {
        public ConcurrentQueue<(string Route, JsonObject Body)> Requests { get; } = new();
        public ConcurrentQueue<string> Events { get; } = new();
        public string DelayedOperation = "";
        public string FailedPikaHeartbeatDevice = "";
        public bool LostAcknowledgement;
        public string? TransportAcknowledgementOverride;
        public bool HoldFailure;
        public bool RejectIdleHold;
        public Func<bool>? ArmActive;
        public bool StreamUnavailable;
        public bool RejectHeartbeatAfterAccepted;
        public string UpdateFailure = "";
        public PikaFollowAudit? PikaFailureAudit;
        public bool FailArmWithPikaAudit;
        public CancellationToken AdmissionCancellation;
        public DateTime? AdmissionDeadline;
        public TaskCompletionSource DelayedReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDelayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<string, JsonObject>? Accepted;
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var command = request as ControlRequest ?? throw new InvalidOperationException("Unexpected fixture RPC");
            var body = JsonNode.Parse(command.Json)!.AsObject(); Requests.Enqueue((command.Route, body));
            string action = body["action"]?.GetValue<string>() ?? "";
            if (command.Route == "/api/command" && action is "hold" or "follow-update" && body["standalone"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Missing standalone declaration in raw arm control");
            string operation = command.Route switch
            {
                "/api/pika/follow-start" => "pika-start",
                "/api/pika/position" => "pika-position",
                "/api/heartbeat" => "heartbeat",
                _ when action == "follow-start" => "arm-start",
                _ => action
            };
            Events.Enqueue("send:" + (command.Route == "/api/pika/hold" ? "pika-hold" : operation));
            async Task<TResponse> Reply()
            {
                if (RejectIdleHold && action == "hold" && ArmActive?.Invoke() != true)
                    return (TResponse)(object)new ControlReply { Status = 400, Json = "{\"error\":\"Confirm that the full motion path is clear and the arm is attended.\"}" };
                if (HoldFailure && action == "hold")
                    return (TResponse)(object)new ControlReply { Status = 409, Json = "{\"error\":\"hold transport failure\"}" };
                if (UpdateFailure.Length > 0 && action == "follow-update")
                    return (TResponse)(object)new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = UpdateFailure }) };
                if (PikaFailureAudit is { } audit && command.Route == "/api/pika/follow-update")
                    return (TResponse)(object)new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = "No matching active Pika following session.", pika_follow_audit = audit }) };
                if (FailArmWithPikaAudit && PikaFailureAudit is not null && action == "follow-update")
                    return (TResponse)(object)new ControlReply { Status = 409, Json = "{\"error\":\"Arm following input expired.\"}" };
                bool failedPika = FailedPikaHeartbeatDevice.Length > 0 && command.Route == "/api/pika/heartbeat" &&
                    body["pika_device"]?.GetValue<string>() == FailedPikaHeartbeatDevice;
                if (failedPika || DelayedOperation.Length > 0 && operation == DelayedOperation)
                {
                    AdmissionCancellation = options.CancellationToken; AdmissionDeadline = options.Deadline;
                    DelayedReached.TrySetResult(); await ReleaseDelayed.Task;
                    Events.Enqueue("accepted:" + operation);
                }
                if (failedPika)
                    return (TResponse)(object)new ControlReply { Status = 409, Json = "{\"error\":\"Pika fixture heartbeat failed\"}" };
                Accepted?.Invoke(command.Route, body);
                if (RejectHeartbeatAfterAccepted && command.Route == "/api/heartbeat")
                    return (TResponse)(object)new ControlReply { Status = 409, Json = "{\"error\":\"No matching active motion lease\"}" };
                if (LostAcknowledgement && operation == DelayedOperation) throw new RpcException(new Status(StatusCode.Cancelled, "Accepted fixture operation lost its acknowledgement"));
                var response = new JsonObject { ["request_id"] = 77, ["motor_request_id"] = "pika-position-fixture", ["follow_session"] = "pika-fixture", ["trajectory_follow"] = true };
                if (action == "follow-start" && (body["trajectory_transport"] is not null || TransportAcknowledgementOverride is not null))
                {
                    response["trajectory_transport"] = TransportAcknowledgementOverride ?? body["trajectory_transport"]!.GetValue<string>();
                    response["trajectory_transport_validated"] = true;
                }
                return (TResponse)(object)new ControlReply { Status = 202, Json = response.ToJsonString() };
            }
            return new(Reply(), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw (StreamUnavailable ? new RpcException(new Status(StatusCode.Unavailable, "fixture transient stream")) : new NotSupportedException());
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}

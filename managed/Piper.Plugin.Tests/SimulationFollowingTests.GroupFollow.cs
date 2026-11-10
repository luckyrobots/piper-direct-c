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
    public static async Task RunGroupFollow()
    {
        static Task WaitFor(Func<bool> condition, string description) => Eventually(condition, description);
        foreach (var (profile, name, transport) in new[] { ("piper-1", "First", SimulationTrajectoryTransport.StreamedPosition),
            ("piper-2", "Second", SimulationTrajectoryTransport.StreamedPosition), ("piper-2", "Second", "unknown-wire-mode") })
        {
            await using var unsupported = new Fixture(); unsupported.ConfigureGroup(); unsupported.UseUiDispatcher();
            unsupported.SetProfileTransport(profile, transport);
            // A backend capability advertisement cannot qualify a transport
            // that this coordinator deliberately does not support.
            unsupported.Edit(snapshot =>
            {
                snapshot["streamed_position_transport_supported"] = true;
                snapshot["streamed_position_wire_validation_supported"] = true;
                foreach (var device in snapshot["devices"]!.AsArray()) device!["streamed_position_supported"] = true;
            });
            Check(unsupported.Preparation().Issues.Any(i => i.ProfileId == profile && i.Code == "transport") &&
                unsupported.SidebarStatus().Contains(name + ": Select position-velocity"),
                "A selected robot's saved unsupported transport was silently ignored");
            await unsupported.Click("group.follow.toggle");
            await WaitFor(() => unsupported.GroupSetupFailure().Length > 0, "Unsupported transport click did not finish");
            Check(!unsupported.GroupActive && unsupported.SidebarStatus().Contains(name + ": Select position-velocity") &&
                unsupported.Rpc.Requests.IsEmpty && !unsupported.EngineRequests.Any(r => r.Action == "arm-next-play"),
                "Unsupported transport armed the engine, enabled experimental control or emitted a target");
            unsupported.SetProfileTransport(profile, SimulationTrajectoryTransport.PositionVelocity);
            await unsupported.Click("group.follow.toggle"); await WaitFor(() => unsupported.GroupArmedForPlay, "Explicit PV correction did not arm");
            Check(unsupported.Mode == "edit" && unsupported.Rpc.Requests.IsEmpty && unsupported.EngineRequests.Count(r => r.Action == "arm-next-play") == 1,
                "Restoring PV silently entered Play, sent a target or armed more than once");
        }
        foreach (string saved in new[] { "valid", "missing", "malformed", "other-profile", "other-device", "other-distribution" })
        {
            await using var reference = new Fixture(); reference.ConfigureGroup(); reference.SelectGroupFollowers("piper-2"); reference.UseUiDispatcher();
            string? distribution = new RobotProfile("piper-2", "Second").PikaDistribution;
            var value = new PikaClosedDisplayReference(1, saved == "other-profile" ? "piper-1" : "piper-2",
                saved == "other-device" ? "/dev/other" : "/dev/pika-b", saved == "other-distribution" ? "other" : distribution, -.0354, DateTimeOffset.UtcNow);
            string path = Path.Combine(reference.Plugin.engine.Start.SettingsDirectory, "gripper-reference", "piper-2.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (saved != "missing") await File.WriteAllTextAsync(path, saved == "malformed" ? "not-json" : JsonSerializer.Serialize(value, PluginProtocol.Json));
            await reference.Click("group.follow.toggle"); await WaitFor(() => reference.GroupArmedForPlay, "Saved-reference fixture did not arm: " + saved);
            var lane = reference.Plugin.follow.Session!.Lanes[0];
            var captured = lane.ClosedReference;
            Check(captured == (saved == "valid" ? -.0354 : null) && reference.Rpc.Requests.IsEmpty,
                "Inlined display-reference load changed missing/invalid fallback or exact profile/device/distribution matching: " + saved);
            await File.WriteAllTextAsync(path, "not-json");
            Check(lane.ClosedReference == captured,
                "An armed lane reread its captured display reference");
        }
        foreach (string scenario in new[] { "missing cache", "stale cache", "invalid current scene" })
        {
            await using var refresh = new Fixture(); refresh.ConfigureGroup(); refresh.UseUiDispatcher();
            var cached = refresh.Plugin.scene!;
            var current = cached.DeepClone().AsObject();
            cached["robots"]![0]!["joints"]!.AsArray().RemoveAt(8);
            if (scenario == "invalid current scene") current["robots"]![0]!["modelRevision"] = "";
            refresh.SetGroupSceneEvidence(scenario == "missing cache" ? null : cached, current);
            Check(!refresh.Preparation().Ready, "The cached scene did not exercise a refreshable blocker: " + scenario);
            await refresh.Click("group.follow.toggle");
            await WaitFor(() => refresh.GroupPhase == GroupFollowPhase.Armed || refresh.GroupSetupFailure().Length > 0,
                "Follow did not finish checking current scene evidence: " + scenario);
            Check(refresh.EngineRequests.Any(r => r.Method == "scene.robots") && refresh.Rpc.Requests.IsEmpty && refresh.Mode == "edit",
                "Scene refresh was skipped or sent a hardware command: " + scenario);
            if (scenario == "invalid current scene")
                Check(!refresh.GroupActive && !refresh.EngineRequests.Any(r => r.Action == "arm-next-play") && refresh.SidebarStatus().Contains("model identity"),
                    "Invalid current scene evidence passed the full arming gate");
            else
                Check(refresh.GroupActive && refresh.EngineRequests.Count(r => r.Action == "arm-next-play") == 1,
                    "A valid current scene did not arm after refresh: " + scenario);
        }
        foreach (string robotId in new[] { "", "robot-a" })
        {
            await using var assignment = new Fixture(); assignment.ConfigureGroup(); assignment.UseUiDispatcher();
            assignment.SetSecondSceneBinding(robotId, "");
            assignment.SetGroupSceneEvidence(null);
            await assignment.Click("group.follow.toggle");
            await WaitFor(() => assignment.GroupSetupFailure().Length > 0, "The static assignment failure did not finish");
            string reason = robotId.Length == 0 ? "Assign this robot's simulation arm" : "Assign a different simulation arm";
            Check(assignment.SidebarStatus().Contains("Second: " + reason) && !assignment.GroupActive && assignment.Rpc.Requests.IsEmpty &&
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
            ("scene-binding", f => f.Plugin.scene!["robots"]![0]!["joints"]!.AsArray().RemoveAt(8)),
        })
        {
            await using var invalid = new Fixture(); invalid.ConfigureGroup(); invalid.Edit(_ => change(invalid));
            var report = invalid.Preparation();
            Check(!report.Ready && report.Issues.Any(i => i.ProfileId == "piper-2" && i.RobotName == "Second" && i.Code == code),
                "Group preparation hid a nonselected robot's named " + code + " blocker");
        }
        await using (var blocked = new Fixture())
        {
            blocked.ConfigureGroup(); blocked.UseUiDispatcher();
            var arm = blocked.Snapshot["devices"]![0]!;
            blocked.Edit(snapshot =>
            {
                arm["status"]!["control"] = 2; arm["status"]!["teach_state"] = 2;
                snapshot["pikas"]![0]!["enabled"] = false;
            });
            Check(blocked.SidebarStatus().Contains("First: Use Resume controls") && blocked.SidebarStatus().Contains("Pika gripper"),
                "The sidebar claimed the selected group was ready while its first arm and Pika were not");
            await blocked.Click("group.follow.toggle");
            await WaitFor(() => blocked.GroupSetupFailure().Length > 0, "The public Follow event did not publish its setup failure");
            Check(!blocked.GroupActive && blocked.SidebarStatus().Contains("Follow not armed") && blocked.SidebarStatus().Contains("First: Use Resume controls") &&
                blocked.SidebarStatus().Contains("Pika gripper") && blocked.Rpc.Requests.IsEmpty && !blocked.EngineRequests.Any(r => r.Action == "arm-next-play"),
                "A public Follow click hid setup failure, armed an unready group or emitted a hardware command");
            blocked.Edit(snapshot => { arm["status"]!["control"] = 1; arm["status"]!["teach_state"] = 0; snapshot["pikas"]![0]!["enabled"] = true; });
            Check(blocked.SidebarStatus().Contains("Follow not armed"), "Telemetry erased the retained setup error before an explicit retry");
            await blocked.Click("group.follow.toggle");
            await WaitFor(() => blocked.GroupPhase == GroupFollowPhase.Armed || blocked.EngineRequests.Any(r => r.Action == "arm-next-play"), "The ready retry never reached the engine arm latch");
            Check(blocked.GroupActive && blocked.Mode == "edit" && blocked.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 && blocked.Rpc.Requests.IsEmpty,
                "Retrying a ready group failed to arm exactly once in Edit or retained a superseded setup failure");
        }
        await using (var stopped = new Fixture())
        {
            stopped.ConfigureGroup(); stopped.UseUiDispatcher();
            stopped.SetGroupSceneEvidence(null);
            stopped.Edit(snapshot =>
            {
                snapshot["devices"]![1]!["status"]!["arm_status"] = 1;
                snapshot["devices"]![1]!["recovery_required"] = true;
            });
            await stopped.Click("group.follow.toggle");
            await WaitFor(() => stopped.GroupSetupFailure().Length > 0, "The stopped partner's public Follow event did not finish");
            Check(stopped.SidebarStatus().Contains("Second: Open recovery") && stopped.Rpc.Requests.IsEmpty &&
                stopped.Snapshot["devices"]![1]!["recovery_required"]!.GetValue<bool>() &&
                !stopped.EngineRequests.Any(r => r.Method == "scene.robots" || r.Action == "arm-next-play"),
                "Follow admission cleared a recorded STOP or concealed the stopped partner");
        }
        await using (var idle = new Fixture())
        {
            idle.ConfigureGroup();
            idle.Edit(snapshot =>
            {
                snapshot["devices"]![0]!["status"]!["stationary_s"] = 0d;
                snapshot["devices"]![0]!["teaching"] = new JsonObject { ["can_return"] = false };
            });
            Check(idle.Preparation().Ready && idle.SidebarFollowEnabled(), "Transient encoder stillness disabled the explicit global Follow action");
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
                Check(!armed.GroupActive && armed.Commands.CurrentCount == 0 && armed.Mode == "edit",
                    "The visible sidebar Stop waited for the command semaphore instead of retiring an armed group immediately");
                Check(armed.Rpc.Requests.IsEmpty && armed.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 &&
                    armed.EngineRequests.Any(r => r.Action == "cancel-start") &&
                    !armed.EngineRequests.Any(r => r.Action is "prepare-start" or "activate-start" or "mark-start-ready" or "pause"),
                    "Cancelling an armed group entered Play, prepared a runtime or emitted a physical target");
            }
            finally { armed.Commands.Release(); }
        }
        await using var f = new Fixture(); f.ConfigureGroup(); f.UseUiDispatcher();
        await f.Click("group.follow.toggle");
        await WaitFor(() => f.GroupActive && f.EngineRequests.Any(r => r.Action == "arm-next-play"), "The public Follow event never armed its ready group");
        Check(f.GroupActive && f.Mode == "edit" && f.Rpc.Requests.IsEmpty &&
            f.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 && !f.EngineRequests.Any(r => r.Action == "prepare-start"),
            "The global arm button entered Play or moved hardware before the user pressed Play");
        f.RequestGroupPlay();
        Task Until(Func<bool> test, string why) => Eventually(test, () => why + " / " + f.Plugin.follow.Status.Headline);
        await Until(() => f.GroupPhase == GroupFollowPhase.Following || !f.GroupActive, "The grouped preparation never activated");
        Check(f.GroupActive && f.Mode == "play", "The group did not activate one shared user-requested Play run: " + f.Plugin.follow.Status.Headline);
        Check(f.Rpc.Requests.Where(r => r.Body["action"]?.GetValue<string>() == "follow-start").Select(r => r.Body["device_id"]!.GetValue<string>()).Order().SequenceEqual(new[] { "can-a", "can-b" }),
            "Both native device lanes were not independently admitted");
        Check(f.Rpc.Requests.Where(r => r.Route == "/api/pika/follow-start").Select(r => r.Body["pika_device"]!.GetValue<string>()).Order().SequenceEqual(new[] { "/dev/pika-a", "/dev/pika-b" }) &&
            f.EngineRequests.Count(r => r.Action == "activate-start") == 1 && !f.Rpc.Requests.Any(r => r.Route == "/api/select"),
            "Grouped following redirected a gripper, switched native selection or activated Play twice");
        f.LoseNextOrderedReply = true;
        await f.OrderedReplyLost.Task.WaitAsync(TimeSpan.FromSeconds(3));
        int publications = f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update");
        int reads = f.CompletedOrderedReads;
        await Task.Delay(80);
        Check(f.GroupActive &&
            f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update") > publications,
            "A delayed grouped read stopped independent lane publications before the retained lease expired");
        await Until(() => f.CompletedOrderedReads > reads || !f.GroupActive, "A lost grouped reply never recovered");
        Check(f.GroupActive && f.EngineRequests.Count(r => r.Method == "robot.runtime.frames" && r.Action != "start" &&
            r.Body["afterFrameCursor"]?.GetValue<string>() == f.LostReplyCursor) >= 2 &&
            f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 2,
            "A150ms grouped read retry skipped its retained cursor, restarted admissions or retired healthy lanes");
        f.TerminalFrame = true; f.Edit(snapshot => snapshot["devices"]![1]!["status"]!["pose_stationary_s"] = 0d);
        await Task.Delay(250);
        Check(!f.EngineRequests.Any(r => r.Action == "ack-episode"), "The shared episode advanced while the second physical arm was not settled");
        await f.Click("profiles.select.piper-2");
        await WaitFor(() => f.Plugin.preferences.ActiveRobotId == "piper-2", "The public profile-selection event was not processed");
        int before = f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update");
        await Until(() => f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update") > before + 2, "Changing the viewed profile stopped group publication");
        Check(f.Rpc.Requests.Where(r => r.Body["action"]?.GetValue<string>() == "follow-update").TakeLast(8).Select(r => r.Body["device_id"]!.GetValue<string>()).Distinct().Count() == 2,
            "Changing profile focus redirected both targets to the visible arm");
        Check(f.Plugin.preferences.ActiveRobotId == "piper-2" && f.GroupActive &&
            f.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 && f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 2,
            "The public profile-selection event changed group ownership or restarted either physical follower");
        f.Edit(snapshot => snapshot["devices"]![1]!["status"]!["pose_stationary_s"] = 10d);
        await Until(() => f.EngineRequests.Any(r => r.Action == "ack-episode"), "All-lane arrival never released its shared episode");
        Check(f.EngineRequests.Count(r => r.Action == "ack-episode") == 1, "The same whole episode was acknowledged separately per arm");
        f.Rpc.HoldFailure = true;
        f.Edit(snapshot => snapshot["devices"]![1]!["generation"] = 9L);
        await Until(() => !f.GroupActive, "A replacement CAN generation did not retire all group workers");
        Check(f.Mode == "pause" && f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold" && r.Body["device_id"]?.GetValue<string>() == "can-a") &&
            f.Rpc.Requests.Where(r => r.Route == "/api/pika/hold").Select(r => r.Body["pika_device"]!.GetValue<string>()).Distinct().Count() == 2,
            "One lane's failed arm hold suppressed independent partner/Pika holds or left simulation running");
        Check(!f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() is "disable" or "reset" || r.Route is "/api/pika/disable" or "/api/pika/disconnect") &&
            f.Plugin.uiActionError.Contains("CAN connection changed"), "Grouped cleanup removed power or lost the actual first generation-change failure");

        var expiryClock = new FollowTestClock();
        await using var expired = new Fixture(followClock: expiryClock); expired.ConfigureGroup();
        await expired.GroupEvent("toggle"); expired.RequestGroupPlay();
        await Eventually(() => expired.GroupPhase == GroupFollowPhase.Following || !expired.GroupActive, "Group expiry fixture did not activate");
        Check(expired.GroupActive, "Group expiry fixture retired before it activated: " + expired.Plugin.follow.Status.Headline);
        expired.LoseNextOrderedReply = true;
        await expired.OrderedReplyLost.Task.WaitAsync(LivenessLimit);
        expired.RuntimeReadTimeout = true;
        expiryClock.Advance(TimeSpan.FromMilliseconds(250));
        expired.Refresh();
        await Eventually(() => !expired.GroupActive, "Repeated read deadlines kept an expired grouped source alive");
        Check(expired.Plugin.uiActionError.Contains("expired after 250 ms", StringComparison.Ordinal) && expired.Mode == "pause",
            $"Repeated grouped retries renewed the original source lease or hid its 250ms expiry: mode {expired.Mode}, error '{expired.Plugin.uiActionError}'");
        Check(expired.Rpc.Requests.Where(r => r.Body["action"]?.GetValue<string>() == "hold").Select(r => r.Body["device_id"]!.GetValue<string>()).Distinct().Count() == 2 &&
            expired.Rpc.Requests.Count(r => r.Route == "/api/pika/hold") == 2,
            "Grouped source expiry did not independently hold all original arm and gripper lanes");
    }
}

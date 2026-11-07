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

internal static class SimulationFollowingTests
{
    public static async Task RunGroupFollow(Action<bool, string> check)
    {
        await using (var armed = new Fixture())
        {
            armed.ConfigureGroup(); armed.UseUiDispatcher();
            await armed.GroupEvent("toggle");
            await armed.Commands.WaitAsync();
            try
            {
                await armed.ClickSidebarGroupStop().WaitAsync(TimeSpan.FromSeconds(2));
                check(!armed.GroupActive && armed.Commands.CurrentCount == 0 && armed.Mode == "edit",
                    "The visible sidebar Stop waited for the command semaphore instead of retiring an armed group immediately");
                check(armed.Rpc.Requests.IsEmpty && armed.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 &&
                    armed.EngineRequests.Any(r => r.Action == "cancel-start") &&
                    !armed.EngineRequests.Any(r => r.Action is "prepare-start" or "activate-start" or "mark-start-ready" or "pause"),
                    "Cancelling an armed group entered Play, prepared a runtime or emitted a physical target");
            }
            finally { armed.Commands.Release(); }
        }
        await using var f = new Fixture(); f.ConfigureGroup();
        await f.GroupEvent("toggle");
        check(f.GroupActive && f.Mode == "edit" && f.Rpc.Requests.IsEmpty &&
            f.EngineRequests.Count(r => r.Action == "arm-next-play") == 1 && !f.EngineRequests.Any(r => r.Action == "prepare-start"),
            "The global arm button entered Play or moved hardware before the user pressed Play");
        f.RequestGroupPlay();
        async Task Until(Func<bool> test, string why)
        {
            long began = Stopwatch.GetTimestamp();
            while (!test()) { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 8) throw new InvalidOperationException(why + " / " + f.Get<string>("GroupFollowStatus")); await Task.Delay(10); }
        }
        await Until(() => f.GroupStage == "following" || !f.GroupActive, "The grouped preparation never activated");
        check(f.GroupActive && f.Mode == "play", "The group did not activate one shared user-requested Play run: " + f.Get<string>("GroupFollowStatus"));
        check(f.Rpc.Requests.Where(r => r.Body["action"]?.GetValue<string>() == "follow-start").Select(r => r.Body["device_id"]!.GetValue<string>()).Order().SequenceEqual(new[] { "can-a", "can-b" }),
            "Both native device lanes were not independently admitted");
        check(f.Rpc.Requests.Where(r => r.Route == "/api/pika/follow-start").Select(r => r.Body["pika_device"]!.GetValue<string>()).Order().SequenceEqual(new[] { "/dev/pika-a", "/dev/pika-b" }) &&
            f.EngineRequests.Count(r => r.Action == "activate-start") == 1 && !f.Rpc.Requests.Any(r => r.Route == "/api/select"),
            "Grouped following redirected a gripper, switched native selection or activated Play twice");
        f.LoseNextOrderedReply = true;
        await f.OrderedReplyLost.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var retainedGroup = f.Get<object>("groupSimulation");
        long envelope = (long)retainedGroup.GetType().GetField("EnvelopeTicks")!.GetValue(retainedGroup)!;
        int publications = f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update");
        int reads = f.CompletedOrderedReads;
        await Task.Delay(80);
        check(f.GroupActive && (long)retainedGroup.GetType().GetField("EnvelopeTicks")!.GetValue(retainedGroup)! == envelope &&
            f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update") > publications,
            "A delayed grouped read refreshed its source lease or stopped independent lane publications prematurely");
        await Until(() => f.CompletedOrderedReads > reads || !f.GroupActive, "A lost grouped reply never recovered");
        check(f.GroupActive && f.EngineRequests.Count(r => r.Method == "robot.runtime.frames" && r.Action != "start" &&
            r.Body["afterFrameCursor"]?.GetValue<string>() == f.LostReplyCursor) >= 2 &&
            f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 2,
            "A150ms grouped read retry skipped its retained cursor, restarted admissions or retired healthy lanes");
        f.Snapshot["devices"]![1]!["status"]!["pose_stationary_s"] = 0d; f.TerminalFrame = true; f.Refresh();
        await Task.Delay(250);
        check(!f.EngineRequests.Any(r => r.Action == "ack-episode"), "The shared episode advanced while the second physical arm was not settled");
        f.FocusGroupSecond();
        int before = f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update");
        await Until(() => f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update") > before + 2, "Changing the viewed profile stopped group publication");
        check(f.Rpc.Requests.Where(r => r.Body["action"]?.GetValue<string>() == "follow-update").TakeLast(8).Select(r => r.Body["device_id"]!.GetValue<string>()).Distinct().Count() == 2,
            "Changing profile focus redirected both targets to the visible arm");
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

        await using var expired = new Fixture(); expired.ConfigureGroup();
        await expired.GroupEvent("toggle"); expired.RequestGroupPlay();
        long started = Stopwatch.GetTimestamp();
        while (expired.GroupStage != "following")
        { if (!expired.GroupActive || Stopwatch.GetElapsedTime(started).TotalSeconds > 5) throw new InvalidOperationException("Group expiry fixture did not activate"); await Task.Delay(10); }
        expired.LoseNextOrderedReply = true;
        await expired.OrderedReplyLost.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var expiringGroup = expired.Get<object>("groupSimulation");
        long original = (long)expiringGroup.GetType().GetField("EnvelopeTicks")!.GetValue(expiringGroup)!;
        expired.RuntimeReadTimeout = true;
        started = Stopwatch.GetTimestamp();
        while (expired.GroupActive)
        { if (Stopwatch.GetElapsedTime(started).TotalSeconds > 3) throw new InvalidOperationException("Repeated read deadlines kept an expired grouped source alive"); await Task.Delay(10); }
        check((long)expiringGroup.GetType().GetField("EnvelopeTicks")!.GetValue(expiringGroup)! == original &&
            expired.Get<string>("uiActionError").Contains("expired after 250 ms", StringComparison.Ordinal) && expired.Mode == "pause",
            "Repeated grouped retries renewed the original source lease or hid its250ms expiry");
        check(expired.Rpc.Requests.Where(r => r.Body["action"]?.GetValue<string>() == "hold").Select(r => r.Body["device_id"]!.GetValue<string>()).Distinct().Count() == 2 &&
            expired.Rpc.Requests.Count(r => r.Route == "/api/pika/hold") == 2,
            "Grouped source expiry did not independently hold all original arm and gripper lanes");
    }
    public static async Task Run(Action<bool, string> check)
    {
        await RunFailureHeader(check);
        await RunPrimaryWorkspaceAction(check);
        await RunZeroSpeed(check);
        await RunTrajectoryTransport(check);
        await RunPreparingReadRetry(check);
        await RunNativeAdmissionReference(check);
        await RunOrderedReadRetry(check);
        await RunReleaseRetirement(check);
        await RunOrderedProtocol(check);
        await RunRetirementDiagnostics(check);
        await using (var navigation = new Fixture())
        {
            await navigation.Start(); await Until(() => navigation.Ready, "Navigation fixture did not become ready");
            check(navigation.TryUserPlay(), "Navigation fixture could not request Play");
            await Until(() => navigation.PlayStarted, "Navigation fixture did not enter Play");
            foreach (string page in new[] { "settings", "recording", "camera", "controls" })
            {
                long before = navigation.Rpc.Requests.LongCount(r => r.Body["action"]?.GetValue<string>() == "follow-update");
                navigation.SetWorkspace(page);
                await Until(() => navigation.Session is null || navigation.Rpc.Requests.LongCount(r => r.Body["action"]?.GetValue<string>() == "follow-update") > before,
                    "Following did not continue while browsing " + page);
                check(navigation.Session is not null, "Browsing " + page + " stopped the approved follower");
            }
            navigation.SetWorkspace("calibration", calibration: true);
            await Until(() => navigation.Session is null, "Read-only calibration did not disarm the follower");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); await Until(() => f.Ready, "Active age fixture did not become Ready");
            check(f.TryUserPlay(), "Active age fixture could not request Play");
            await Until(() => f.PlayStarted, "Active age fixture did not enter Play");
            var diagnostics = (SimulationFollowDiagnostics)f.Session!.GetType().GetField("Diagnostics")!.GetValue(f.Session)!;
            f.Snapshot["pika"]!["age_s"] = .3; f.Refresh();
            await Until(() => f.Session is null, "Stale active Pika was softened into an inactive Ready wait");
            await diagnostics.Completion;
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(diagnostics.Pathname))!;
            var health = saved["firstFailure"]!["Hardware"]!["Pika"]!;
            check(f.Mode == "pause" && f.Rpc.Requests.Count(r => r.Route == "/api/pika/follow-start") == 1 &&
                health["OwnerMatches"]!.GetValue<bool>() && health["Enabled"]!.GetValue<bool>() &&
                health["SourceAndTransportAgeMilliseconds"]!.GetValue<double>() >= 300 && health["CombinedEvidenceAgeMilliseconds"]!.GetValue<double>() >= 300,
                "Active250ms guard or its Pika age/health evidence was lost");
        }
        foreach (string stale in new[] { "pika", "can", "envelope", "view", "between-read", "stream", "pika-heartbeat", "standby" })
        {
            await using var f = new Fixture();
            if (stale == "standby") f.Snapshot["devices"]![0]!["status"]!["control"] = 0;
            await f.Start(); await Until(() => f.Ready, "Ready freshness fixture did not reach its gate");
            long permission = f.Get<long>("targetPermissionEpoch");
            if (stale == "pika") f.Snapshot["pika"]!["age_s"] = .3;
            if (stale == "can") f.Snapshot["devices"]![0]!["fresh"] = false;
            if (stale == "envelope") f.EnvelopeAgeMs = 650;
            if (stale == "view") f.RuntimeReadTimeout = true;
            if (stale == "between-read") f.StalePikaAfterRead = true;
            if (stale == "stream") f.Rpc.StreamUnavailable = true;
            if (stale == "pika-heartbeat") { f.Rpc.PikaHeartbeatUnavailable = true; f.Snapshot["pika"]!["age_s"] = .3; }
            f.Refresh();
            if (stale == "envelope") await f.Gap("ArmHeartbeats");
            if (stale == "stream") await f.Gap("ReadTelemetry");
            if (stale == "pika-heartbeat") await f.Gap("PikaHeartbeats");
            if (stale != "standby") await Until(() => f.Get<string>("simulationStatus").StartsWith("Ready — press Play · waiting for fresh"), "Inactive Ready did not preserve its prepared Play gate while waiting: " + stale);
            if (stale != "standby") check(f.TryUserPlay(), "Delayed Ready could not retain a paused Play request");
            await Task.Delay(180);
            check(f.Session is not null && f.Mode == "pause" && f.Rpc.Requests.All(r => r.Route == "/api/pika/heartbeat") &&
                f.Get<long>("targetPermissionEpoch") == permission && f.Get<bool>("pikaOwned"),
                "Transient inactive Ready evidence revoked scope or admitted hardware: " + stale);
            f.EnvelopeAgeMs = 0; f.RuntimeReadTimeout = f.StalePikaAfterRead = false;
            f.Snapshot["pika"]!["age_s"] = .001; f.Snapshot["devices"]![0]!["fresh"] = true; f.Refresh();
            if (stale == "standby") check(f.TryUserPlay(), "Healthy standby Ready could not request Play");
            await Until(() => f.PlayStarted || f.Session is null, "Fresh Ready did not process its paused Play intent");
            check(f.PlayStarted && f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 1,
                "Fresh unchanged Ready failed to recover through one admission: " + stale + " · " + f.Get<string>("simulationDetail"));
        }
        foreach (string change in new[] { "generation", "owner", "power", "pika-driver-fault", "pika-enabled-fault", "fault", "pose" })
        {
            await using var f = new Fixture(); await f.Start(); await Until(() => f.Ready, "Hard-change fixture did not reach Ready");
            f.Snapshot["pika"]!["age_s"] = .3;
            if (change == "generation") f.Snapshot["devices"]![0]!["generation"] = 8L;
            if (change == "owner") f.Snapshot["pika"]!["owner"] = "another-owner";
            if (change == "power") f.Snapshot["pika"]!["enabled"] = false;
            if (change == "pika-driver-fault")
            {
                f.Snapshot["pika"]!["enabled"] = false;
                f.Snapshot["pika"]!["driver_status_text"] = "Pika driver fault (0x20).";
                f.Snapshot["pika"]!["telemetry"]!["motor"]!["Position"] = -2d;
            }
            if (change == "pika-enabled-fault")
            {
                f.Snapshot["pika"]!["driver_fault"] = true;
                f.Snapshot["pika"]!["driver_status_text"] = "Pika driver fault (0x60).";
            }
            if (change == "fault") f.Snapshot["devices"]![0]!["status"]!["error"] = 1;
            if (change == "pose") f.Snapshot["devices"]![0]!["joints"]![0]!["actual_deg"] = 40d;
            f.Refresh();
            await Until(() => f.Session is null, "Known hard change was softened into a freshness wait: " + change);
            check(!f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "follow-start" || r.Route == "/api/pika/follow-start"),
                "A changed identity/owner/power/fault/pose admitted hardware after a Ready wait");
            if (change == "pika-driver-fault")
                check(f.Get<string>("simulationDetail").Contains("Pika driver fault (0x20)"),
                    "A reported Pika driver fault was hidden behind a generic owner or position error");
            if (change == "pika-enabled-fault")
                check(f.Get<string>("simulationDetail").Contains("Pika driver fault (0x60)"),
                    "A Pika enabled flag hid a simultaneous gripper-driver fault");
        }
        foreach (bool timeout in new[] { false, true })
        {
            await using var f = new Fixture();
            await f.Start(); await Until(() => f.Ready, "Diagnostic fixture did not become ready");
            var diagnostics = (SimulationFollowDiagnostics)f.Session!.GetType().GetField("Diagnostics")!.GetValue(f.Session)!;
            check(f.TryUserPlay(), "Diagnostic fixture could not request Play");
            await Until(() => f.PlayStarted, "Diagnostic fixture never activated Play");
            f.Rpc.HoldFailure = true;
            if (timeout) f.RuntimeReadTimeout = true;
            else f.Rpc.UpdateFailure = "Following input expired after 250 ms. fixture-backend " + f.Get<string>("owner");
            await Until(() => f.Session is null, "Follow diagnostic failure did not retire its session");
            await diagnostics.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(diagnostics.Pathname))!;
            var trigger = saved["firstFailure"]!;
            string reason = trigger["Reason"]!.GetValue<string>();
            string expectedReason = timeout ? "source observation expired after 250 ms" : "input expired after 250 ms";
            check(reason.Contains(expectedReason),
                "Cleanup replaced the first Follow trigger: " + reason);
            check(saved["cleanupFailures"]!.AsArray().Any(e => e!.GetValue<string>().Contains("hold transport failure")) &&
                f.Get<string>("simulationDetail").Contains(expectedReason),
                "Failed release lost the original trigger or its separate cleanup evidence");
            check(trigger["ArmRequestId"]!.GetValue<long>() == 77 && trigger["FollowSequence"]!.GetValue<long>() > 0 &&
                trigger["Scope"]!["CanDeviceId"]!.GetValue<string>() == "can-a" &&
                trigger["Scope"]!["CanGeneration"]!.GetValue<long>() == 7 &&
                trigger["Scope"]!["RunId"]!.GetValue<string>() == "run-a" && trigger["Runtime"] is not null && trigger["Hardware"] is not null,
                "First Follow failure omitted its exact runtime, real device, request or sequence scope");
            string document = saved.ToJsonString();
            check(!document.Contains("fixture-backend") && !document.Contains(f.Get<string>("owner")) &&
                !document.Contains("telemetry") && f.EngineReports.Any(report => report.Contains(expectedReason)),
                "Follow diagnostics leaked authorization/full state or failed to emit its SDK log");
            check(f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "hold") >= 2 &&
                f.EngineRequests.Any(r => r.Action == "cancel-start") && !f.EngineRequests.Any(r => r.Action == "resume"),
                "Recording a Follow failure defeated teardown or resumed simulation");
            check(f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 1 &&
                f.Rpc.Requests.Count(r => r.Route == "/api/pika/follow-start") == 1,
                "An expired active following session was automatically restarted");
        }
        await using (var f = new Fixture())
        {
            f.Snapshot["trajectory_follow_supported"] = false;
            bool rejected = false;
            try { await f.Start(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && f.Rpc.Requests.IsEmpty && f.EngineRequests.IsEmpty && f.Session is null,
                "An unsupported backend prepared physical simulation following");
        }
        await using (var f = new Fixture())
        {
            f.PreparationSupported = false;
            bool rejected = false;
            try { await f.Start(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && f.Rpc.Requests.IsEmpty && !f.EngineRequests.Any(r => r.Action == "prepare-start") &&
                f.Get<string>("simulationDetail").Contains("Update Lucky Engine"),
                "An old engine fell back to manual Play or sent hardware commands");
        }
        await using (var f = new Fixture(mismatch: true))
        {
            f.ActivationSupported = false;
            bool rejected = false;
            try { await f.Start(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && f.Rpc.Requests.IsEmpty && !f.EngineRequests.Any(r => r.Action == "prepare-start"),
                "An engine without a Play admission gate moved hardware or used the legacy unsafe Ready path");
        }
        foreach (bool stopBeforeAcknowledgement in new[] { false, true })
        {
            await using var f = new Fixture(); f.DelayedRuntimeAction = "activate-start";
            await f.Start(); await Until(() => f.Ready, "Activation delay fixture did not become ready");
            check(f.Rpc.Requests.IsEmpty && f.Mode == "pause" && f.TryUserPlay(), "Ready used hardware before Play intent");
            await f.RuntimeReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            int updates = f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update");
            for (int i = 0; i < 3; i++) { await Task.Delay(80); f.Refresh(); } // Stay comfortably within the400ms ACK deadline under concurrent engine builds.
            check(f.Mode == "pause" && !f.PlayStarted &&
                f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update") > updates &&
                f.Rpc.Requests.Any(r => r.Route == "/api/pika/follow-update"),
                "A blocked activation RPC starved the admitted static hardware targets or advanced simulation");
            if (stopBeforeAcknowledgement) await f.StopEvent();
            f.ReleaseRuntime.TrySetResult();
            if (stopBeforeAcknowledgement)
            {
                await Until(() => f.Session is null, "Late activation ACK did not retire after Stop");
                check(f.Mode == "pause" && !f.AppliedEngineRequests.Any(r => r.Action == "activate-start"),
                    "A canceled late activation acknowledgement started the scene");
            }
            else
            {
                await Until(() => f.PlayStarted || f.Session is null, "Acknowledged activation did not finish");
                check(f.PlayStarted, "Acknowledged activation did not start linked Play: " + f.Get<string>("simulationDetail"));
                check(f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 1 &&
                    f.Rpc.Requests.Count(r => r.Route == "/api/pika/follow-start") == 1,
                    "Slow activation restarted an active hardware session");
            }
        }
        await using (var f = new Fixture())
        {
            await f.Start(); await Until(() => f.Ready, "Changed activation fixture did not become ready");
            f.ChangeActivationFrame = true; check(f.TryUserPlay(), "Changed activation fixture could not request Play");
            await Until(() => f.Session is null, "A changed activation frame retained following authority");
            check(f.Get<string>("simulationDetail").Contains("first frame") && f.Mode == "pause" &&
                f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 1,
                "An advanced/changed activation frame was replayed or its failure hidden");
        }
        foreach (var (jawAgeSeconds, envelopeAgeMs) in new[] { (.3, 0d), (.2, 60d), (.001, 260d) })
        {
            await using var f = new Fixture(mismatch: true);
            f.Snapshot["pika"]!["age_s"] = jawAgeSeconds; f.EnvelopeAgeMs = envelopeAgeMs; f.Refresh();
            check(f.ReadinessBlocker().Contains("250 ms"), "Follow readiness hid accumulated Pika evidence age");
            bool rejected = false;
            try { await f.Start(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && f.Rpc.Requests.IsEmpty && !f.EngineRequests.Any(r => r.Action == "prepare-start") && f.Session is null,
                "Automatic matching began with stale jaw evidence in a fresh controller envelope");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); await Until(() => f.Ready, "Pika freshness fixture did not become ready");
            f.Snapshot["pika"]!["age_s"] = .3;
            await Until(() => f.Get<string>("simulationStatus").StartsWith("Ready — press Play · waiting for fresh"), "Stale Pika evidence did not wait in inactive Ready");
            check(f.Session is not null && f.Mode == "pause" && !f.EngineRequests.Any(r => r.Action == "activate-start"),
                "Stale Pika feedback admitted hardware or resumed the prepared run");
        }
        await using (var f = new Fixture(mismatch: true))
        {
            f.AutoPrepare = false;
            f.Snapshot["pika"]!["telemetry"]!["motor"]!["Position"] = 0d;
            await f.PublishMeasured();
            var closed = f.EngineRequests.Single(r => r.Method == "robot.feedback").Body["joints"]!;
            check(closed["joint7"]!.GetValue<double>() < -.04 && closed["joint8"]!.GetValue<double>() > .04,
                "Closed physical Pika did not set the measured Edit jaw pose");
            await f.Start();
            await Task.Delay(120);
            check(f.Mode == "preparing" && f.Rpc.Requests.IsEmpty && !f.TryUserPlay(),
                "Preparing moved hardware or permitted Play before its first frame was ready");
            f.Mode = "pause";
            await Until(() => f.Ready, "Automatic first-pose matching never became ready");
            check(f.Mode == "pause" && f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "pose") == 1 &&
                f.Rpc.Requests.Count(r => r.Route == "/api/pika/position") == 1 &&
                f.Rpc.Requests.Single(r => r.Body["action"]?.GetValue<string>() == "pose").Body["speed_percent"]?.GetValue<double>() == 10 &&
                !f.EngineRequests.Any(r => r.Action == "resume"),
                "Follow failed to match once at 10% or automatically started playback");
            check(f.Get<string>("simulationStartComparison").Contains("J6") && f.Get<string>("simulationStartComparison").Contains("Pika") &&
                f.Get<string>("simulationStatus").StartsWith("Ready"), "Ready UI omitted joint/gripper comparison or its Play instruction");
            int updates = f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update");
            await Task.Delay(3250);
            check(f.Session is not null && f.Mode == "pause" && f.Ready &&
                f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-update") == updates &&
                !f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "follow-start" || r.Route == "/api/pika/follow-start") &&
                !f.EngineRequests.Any(r => r.Action == "resume"),
                "Indefinite Ready admitted active hardware follow, sent updates or resumed without user Play");
            int feedback = f.EngineRequests.Count(r => r.Method == "robot.feedback");
            await f.PublishMeasured();
            check(f.EngineRequests.Count(r => r.Method == "robot.feedback") == feedback,
                "Measured feedback overwrote the prepared runtime leader");
            check(f.TryUserPlay(), "Ready did not permit the user's Play");
            await Until(() => f.PlayStarted, "The user's Play failed after an indefinite ready wait");
            check(!f.EngineRequests.Any(r => r.Action == "resume") && f.EngineRequests.Count(r => r.Action == "activate-start") == 1 &&
                f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 1 &&
                f.Rpc.Requests.Count(r => r.Route == "/api/pika/follow-start") == 1,
                "User Play did not admit each controller once through the explicit activation handshake");
            string nonce = f.EngineRequests.Single(r => r.Action == "prepare-start").Body["prepareId"]!.GetValue<string>();
            check(nonce.Length == 32 && f.EngineRequests.Single(r => r.Action == "prepare-start").Body["activationHandshake"]?.GetValue<bool>() == true &&
                f.EngineRequests.Where(r => r.Method == "robot.runtime.control").All(r =>
                r.Body["prepareId"]?.GetValue<string>() == nonce), "Preparation controls lost their exact Follow nonce");
        }
        foreach (int evidence in new[] { 1, 2, 4 })
        {
            await using var f = new Fixture();
            f.PikaEvidenceMode = evidence;
            f.Snapshot["pika"]!["telemetry"]!["motor"]!["Position"] = 0d;
            await f.Start();
            await Until(() => f.Rpc.Requests.Any(r => r.Route == "/api/pika/position"), "Pika evidence fixture did not issue its match target");
            await Task.Delay(260);
            check(!f.Ready && !f.Rpc.Requests.Any(r => r.Route == "/api/pika/follow-start"),
                "A stale idle snapshot, wrong mutation evidence or repeated motor sample bypassed Pika match completion: " + evidence);
            f.PikaEvidenceMode = 3;
            await Task.Delay(150);
            check(!f.Ready && !f.Rpc.Requests.Any(r => r.Route == "/api/pika/follow-start"),
                "Pika entered follow while its acknowledged position move was still pending");
            f.PikaEvidenceMode = 0;
            await Until(() => f.Ready, "Confirmed post-request Pika idle feedback did not release the start barrier");
            check(f.Rpc.Requests.Count(r => r.Route == "/api/pika/position") == 1 && f.Mode == "pause",
                "Waiting for Pika idle repeated its physical move or resumed Play");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); await Until(() => f.Ready, "An already matched start did not become ready");
            check(!f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "pose" || r.Route == "/api/pika/position") && f.Mode == "pause",
                "An already matched start unnecessarily moved the arm/Pika or resumed Play");
            await f.StopEvent();
            check(f.Session is null && !f.Ready && f.Mode == "pause" && f.Scale == 1 &&
                f.EngineRequests.Count(r => r.Action == "cancel-start") >= 2 && f.Rpc.Requests.IsEmpty,
                "Stopping an already matched Ready run sent hardware commands or failed to restore the paused preparation");
        }
        foreach (string? timing in new string?[] { "deterministic-fast", "unknown", null })
        {
            await using var f = new Fixture(); f.TimeMode = timing;
            bool rejected = false;
            try { await f.Start(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && f.Session is null && f.Rpc.Requests.IsEmpty && !f.EngineRequests.Any(r => r.Action == "prepare-start"),
                "An unpaced scene mode prepared physical following: " + (timing ?? "missing"));
        }
        foreach (bool inconsistent in new[] { false, true })
        {
            await using var f = new Fixture();
            f.InvalidModel = !inconsistent; f.UnsettledStart = inconsistent;
            await f.Start(); await Until(() => f.Session is null, "Invalid start/model did not stop preparation");
            check(f.Rpc.Requests.IsEmpty && !f.Ready, "An invalid actuator law or unsettled simulated start moved hardware");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); await Until(() => f.Ready, "Changed-frame fixture did not become ready");
            f.UnsettledStart = true;
            await Until(() => f.Session is null, "A changed paused first frame remained ready");
            check(f.Mode == "pause" && f.Get<string>("simulationDetail").Contains("first frame"),
                "A changed paused first frame resumed or hid its reason");
        }
        foreach (string delayed in new[] { "pose", "pika-position", "pika-start", "arm-start" })
        foreach (bool lostAck in new[] { false, true })
        {
            await using var f = new Fixture(mismatch: delayed == "pose");
            if (delayed == "pika-position") f.Snapshot["pika"]!["telemetry"]!["motor"]!["Position"] = 0d;
            f.Rpc.DelayedOperation = delayed; f.Rpc.LostAcknowledgement = lostAck;
            await f.Start();
            if (delayed is "pika-start" or "arm-start")
            { await Until(() => f.Ready, "Delayed admission did not reach Ready"); check(f.TryUserPlay(), "Delayed admission could not request Play"); }
            await f.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(4));
            object? old = f.Session;
            await f.StopEvent().WaitAsync(TimeSpan.FromSeconds(4));
            check(old is not null && ReferenceEquals(old, f.Session) && f.Ending && f.Get<long>("lease") == 0,
                "Stop cleared a still-admitting preparation or renewed its old lease: " + delayed);
            int preparations = f.EngineRequests.Count(r => r.Action == "prepare-start");
            await f.Start();
            check(ReferenceEquals(old, f.Session) && f.EngineRequests.Count(r => r.Action == "prepare-start") == preparations,
                "A new Follow replaced a still-admitting preparation: " + delayed);
            if (delayed == "pose") f.FreezeTelemetry = true;
            f.Rpc.ReleaseDelayed.TrySetResult();
            await Until(() => f.Session is null, "Late preparation acknowledgement did not retire");
            var events = f.Rpc.Events.ToArray(); int accepted = Array.FindIndex(events, e => e == "accepted:" + delayed);
            check(accepted >= 0 && (delayed is not ("arm-start" or "pose") || events.Skip(accepted + 1).Contains("send:hold")) && events.Skip(accepted + 1).Contains("send:pika-hold") &&
                !f.EngineRequests.Any(r => r.Action is "activate-start" or "resume"),
                "Late preparation admission escaped final holds or activated/resumed Play: " + delayed);
            if (delayed == "pika-position")
                check(!f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold"),
                    "Pika-only setup failure sent an unsupported hold command to an untouched idle arm");
        }
        await using (var f = new Fixture())
        {
            f.DelaySetup = true; Task starting = f.Start();
            await f.SetupReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await f.StopEvent(); await f.PublishMeasured();
            check(f.Ending && !f.EngineRequests.Any(r => r.Method == "robot.feedback") && f.Rpc.Requests.IsEmpty,
                "Canceled native preparation reapplied measured feedback or moved hardware");
            f.ReleaseSetup.TrySetResult();
            bool rejected = false;
            try { await starting; } catch (InvalidOperationException) { rejected = true; }
            await Until(() => f.Session is null, "Late native preparation did not retire");
            check(rejected && f.Mode == "edit" && !f.Ready && f.Rpc.Requests.IsEmpty,
                "A canceled native preparation later created a runtime or moved hardware");
        }
        await using (var f = new Fixture())
        {
            f.DelayedRuntimeAction = "mark-start-ready";
            await f.Start(); await f.RuntimeReached.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await f.StopEvent(); f.ReleaseRuntime.TrySetResult();
            await Until(() => f.Session is null, "A late ready acknowledgement did not retire");
            check(!f.Ready && f.Mode == "pause" && !f.EngineRequests.Any(r => r.Action == "resume"),
                "A canceled late ready acknowledgement unlocked Play or resumed the episode");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); await Until(() => f.Ready, "Replacement-run fixture did not become ready");
            f.RunId = "replacement-run"; f.Scale = .9; f.Mode = "play";
            await f.StopEvent();
            check(f.Session is null && f.RunId == "replacement-run" && f.Mode == "play" && f.Scale == .9,
                "Old preparation cleanup paused or changed a replacement run");
        }
        async Task Until(Func<bool> ready, string message)
        {
            long began = Stopwatch.GetTimestamp();
            while (!ready()) { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5) throw new InvalidOperationException(message); await Task.Delay(10); }
        }
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
            Task resume = f.PrimaryResume();
            for (int i = 0; i < 3; i++) { f.Refresh(); await Task.Delay(35); }
            check(!resume.IsCompleted && !f.Get<bool>("approved") && f.Get<bool>("resumeControlsBusy"),
                "CAN mode alone completed Resume before the native handoff acknowledgement");
            f.CompleteResume();
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
            f.InterruptResume(interruption); f.Refresh();
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
                ? confirmed.Get<string>("primaryActionStatus").StartsWith("Could not start:", StringComparison.Ordinal) && result["failure"]!.GetValue<string>().Contains("hand guiding", StringComparison.Ordinal)
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
    public static async Task RunRetirementDiagnostics(Action<bool, string> check)
    {
        foreach (bool bothChannels in new[] { false, true })
        {
        await using var f = new Fixture();
        await f.Start();
        async Task Until(Func<bool> predicate)
        {
            long began = Stopwatch.GetTimestamp();
            while (!predicate()) { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5) throw new InvalidOperationException("Retirement diagnostics fixture timed out · " + f.Get<string>("simulationDetail")); await Task.Delay(10); }
        }
        await Until(() => f.Ready); check(f.TryUserPlay(), "Retirement diagnostics fixture could not request Play");
        await Until(() => f.PlayStarted);
        var diagnostics = (SimulationFollowDiagnostics)f.Session!.GetType().GetField("Diagnostics")!.GetValue(f.Session)!;
        var following = new PikaFollowSession(_ => { });
        string id = following.Start(0); following.Update(id, 12, 20, .01); following.Tick(.27, PikaProtocol.Angle(20), true);
        f.Rpc.FailArmWithPikaAudit = bothChannels;
        f.Rpc.PikaFailureAudit = following.Audit;
        await Until(() => f.Session is null); await diagnostics.Completion;
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(diagnostics.Pathname))!["firstFailure"]!;
        check(saved["Hardware"]!["Pika"]!["FollowAudit"]!["LastRetirement"]!["Cause"]!.GetValue<string>() == "input-expired" &&
            saved["Hardware"]!["Pika"]!["FollowAudit"]!["LastRetirement"]!["LastInputSequence"]!.GetValue<long>() == 12 &&
            (bothChannels || saved["Reason"]!.GetValue<string>().Contains("Previous stream ended:")),
            "Immediate negative Pika ACK retirement evidence was replaced by older telemetry or hold cleanup");
        var publications = saved["Publications"]!["RecentPublications"]!.AsArray();
        check(publications.Count is > 0 and <= 16 && publications.Any(p => p!["PikaErrorType"]?.GetValue<string>() == nameof(PikaFollowRejectedException)) &&
            saved["Publications"]!["MaximumArmAcknowledgementMilliseconds"]!.GetValue<double>() >= 0,
            "First failure did not preserve bounded per-channel publication and ACK timings");
        check(f.Rpc.Requests.Count(r => r.Route == "/api/pika/follow-start") == 1 && f.Mode == "pause",
            "Diagnostics automatically restarted expired Pika input or defeated coordinated holding");
        }
    }

    public static async Task RunOrderedProtocol(Action<bool, string> check)
    {
        static async Task Until(Func<bool> predicate, string reason)
        {
            long began = Stopwatch.GetTimestamp();
            while (!predicate()) { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5) throw new InvalidOperationException(reason); await Task.Delay(10); }
        }
        await using (var f = new Fixture(mismatch: true))
        {
            f.FramesSupported = false;
            bool rejected = false; try { await f.Start(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && f.Rpc.Requests.IsEmpty && !f.EngineRequests.Any(r => r.Action == "prepare-start"),
                "An engine without an ordered source tape admitted a match or fell back to latest-wins following");
        }
        foreach (string error in new[] { "cursor", "manual-pause", "stale-source" })
        {
            await using var f = new Fixture();
            await f.Start(); await Until(() => f.Ready, "Ordered protocol did not reach Ready");
            var calls = f.EngineRequests.ToArray();
            int claimed = Array.FindIndex(calls, r => r.Action == "claim-episode-follower"), started = Array.FindIndex(calls, r => r.Method == "robot.runtime.frames" && r.Action == "start"), ready = Array.FindIndex(calls, r => r.Action == "mark-start-ready");
            check(claimed >= 0 && started > claimed && ready > started &&
                calls[started].Body["jointNames"]!.AsArray().Count == 6 && calls[started].Body["gripperMapping"]!["commandScaleMm"]!.GetValue<int>() == 2000 && f.Rpc.Requests.IsEmpty,
                "Prepared source tape/episode ownership was not pinned before Ready or moved hardware while waiting");
            check(f.TryUserPlay(), "Ordered protocol could not request Play");
            await Until(() => f.PlayStarted, "Ordered protocol did not activate");
            if (error == "cursor") f.FrameCursorGap = true;
            if (error == "manual-pause") f.Mode = "pause";
            if (error == "stale-source") f.FrameSourceAgeMs = 300;
            await Until(() => f.Session is null, "Ordered protocol retained motion after " + error);
            check(f.Mode == "pause" && f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold") &&
                f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "follow-start") == 1,
                "A source cursor/freshness/manual-pause failure restarted motion or escaped verified holds");
        }
        await using (var f = new Fixture())
        {
            f.EpisodeId = "episode-a";
            await f.Start(); await Until(() => f.Ready, "Episode barrier fixture did not become Ready");
            check(f.TryUserPlay(), "Episode barrier fixture could not request Play");
            await Until(() => f.PlayStarted, "Episode barrier fixture did not activate");
            f.TerminalFrame = true;
            f.Snapshot["devices"]![0]!["status"]!["stationary_s"] = 1d;
            f.Snapshot["devices"]![0]!["status"]!["pose_stationary_s"] = 0d; f.Refresh();
            await Task.Delay(230);
            check(!f.EngineRequests.Any(r => r.Action == "ack-episode"), "An exported source episode advanced before physical arm stationarity");
            f.Snapshot["devices"]![0]!["status"]!["stationary_s"] = 0d;
            f.Snapshot["devices"]![0]!["status"]!["pose_stationary_s"] = 67d; f.Refresh();
            await Until(() => f.EngineRequests.Any(r => r.Action == "ack-episode"), "Physical final arrival never acknowledged its exact episode");
            var acknowledged = f.EngineRequests.Single(r => r.Action == "ack-episode").Body;
            check(acknowledged["episodeId"]!.GetValue<string>() == "episode-a" && acknowledged["episodeOrdinal"]!.GetValue<long>() == 0 &&
                ulong.Parse(acknowledged["finalFrameCursor"]!.GetValue<string>()) == f.SealedFrameCursor && f.Scale == 1 &&
                f.EngineRequests.Single(r => r.Action == "activate-start").Body["timeScale"]!.GetValue<double>() == 1 &&
                !f.EngineRequests.Any(r => r.Action == "resume"),
                "Physical completion lost exact final source cursor/episode identity or kept the leader at20% speed");
            await Task.Delay(150);
            check(f.Session is not null && f.EngineRequests.Count(r => r.Action == "ack-episode") == 1,
                "A sealed source episode stalled/duplicated its physical completion acknowledgement");
        }
        foreach (bool sourceMiss in new[] { false, true })
        {
            await using var f = new Fixture { EpisodeId = "episode-a", ScriptedMissedGrasp = true, ScriptedSourceMiss = sourceMiss };
            f.Snapshot["devices"]![0]!["status"]!["stationary_s"] = 0d;
            f.Snapshot["devices"]![0]!["status"]!["pose_stationary_s"] = 67d;
            foreach (var joint in f.Snapshot["devices"]![0]!["joints"]!.AsArray().OfType<JsonObject>())
            { joint["actual_deg"] = 0d; joint["command_deg"] = 0d; }
            f.Refresh();
            await f.Start(); await Until(() => f.Ready, "Missed-grasp cycle did not reach Ready");
            check(f.TryUserPlay(), "Missed-grasp cycle could not request Play");
            await Until(() => f.Session is null, "Missed-grasp cycle did not finish its original home path and release controls");
            check(f.Get<string>("simulationStatus").Contains("Grasp not confirmed") && f.Get<string>("simulationStatus").Contains("home") &&
                !f.EngineRequests.Any(r => r.Action == "ack-episode") && f.EngineRequests.Any(r => r.Action == "cancel-start"),
                "A source or physical missed grasp claimed success, released another episode, or retained control ownership: " +
                    f.Get<string>("simulationStatus") + " / " + f.Get<string>("simulationDetail"));
            var openings = f.Rpc.Requests.Where(r => r.Route == "/api/pika/follow-update").Select(r => r.Body["opening_mm"]!.GetValue<double>()).ToArray();
            check(openings.Contains(10) && openings.Last() == 20 && f.Mode == "pause" &&
                f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold") &&
                f.Snapshot["devices"]![0]!["joints"]!.AsArray().All(j => j!["enabled"]!.GetValue<bool>()),
                "A missed grasp stopped before ordered release/home, skipped a jaw command, or disabled holding motors");
        }
    }

    public static async Task RunFailureHeader(Action<bool, string> check)
    {
        await using var f = new Fixture();
        await f.Start();
        long began = Stopwatch.GetTimestamp();
        async Task Until(Func<bool> condition)
        {
            while (!condition())
            { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 6) throw new InvalidOperationException("First failure header fixture timed out"); await Task.Delay(10); }
        }
        await Until(() => f.Ready); check(f.TryUserPlay(), "First failure header fixture could not request Play");
        await Until(() => f.PlayStarted);
        var session = f.Session!;
        f.Rpc.HoldFailure = true;
        f.Rpc.UpdateFailure = "Ordered arm endpoint did not settle: fixture-backend";
        await Until(() => f.Session is null);
        string first = f.Get<string>("uiActionError");
        check(first.Contains("Ordered arm endpoint did not settle") && !first.Contains("fixture-backend") && !first.Contains("hold transport failure"),
            "Cleanup acknowledgements hid the first follower failure or leaked its backend identity in the header");
        f.Refresh(); await Task.Delay(30);
        check(f.Get<string>("uiActionError") == first, "Fresh holding telemetry erased the first failure from the header");
        f.ClearHeaderAndRecordLateFailure(session);
        check(f.Get<string>("uiActionError").Length == 0, "An old cleanup worker resurrected a failure after a new user action cleared it");
    }
    public static async Task RunNativeAdmissionReference(Action<bool, string> check)
    {
        static async Task Until(Func<bool> predicate, string reason)
        {
            long began = Stopwatch.GetTimestamp();
            while (!predicate()) { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5) throw new InvalidOperationException(reason); await Task.Delay(5); }
        }
        await using (var f = new Fixture())
        {
            f.DelayNativeAdmissionTelemetry = true;
            await f.Start(); await Until(() => f.Ready, "Native admission fixture did not become Ready");
            check(f.TryUserPlay(), "Native admission fixture could not request Play");
            await Until(() => f.Session is not null && (string)f.Session.GetType().GetField("Operation")!.GetValue(f.Session)! == "await-native-follow-reference",
                "Native admission never reached its telemetry barrier");
            int updates = f.Rpc.Requests.Count(request => request.Body["action"]?.GetValue<string>() == "follow-update");
            long began = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(began).TotalMilliseconds < 100) { f.Refresh(); await Task.Delay(5); }
            check(f.Session is not null && f.Mode == "pause" && !f.PlayStarted &&
                !f.EngineRequests.Any(request => request.Action == "activate-start") &&
                f.Rpc.Requests.Count(request => request.Body["action"]?.GetValue<string>() == "follow-update") > updates,
                "Cached pre-ACK native frames must keep the exact source paused while static priming renews the admitted hardware leases");
            f.PublishNativeAdmissionReference();
            await Until(() => { f.Refresh(); return f.PlayStarted || f.Session is null; }, "Fresh native admission feedback did not release its barrier");
            check(f.PlayStarted && f.Rpc.Requests.Count(request => request.Body["action"]?.GetValue<string>() == "follow-start") == 1 &&
                f.Rpc.Requests.Count(request => request.Route == "/api/pika/follow-start") == 1,
                "One fresh exact-request native reference must activate the prepared source without restarting either hardware stream");
        }
        foreach (string failure in new[] { "timeout", "owner", "stop" })
        {
            await using var f = new Fixture(); f.DelayNativeAdmissionTelemetry = true;
            await f.Start(); await Until(() => f.Ready, "Native failure fixture did not become Ready");
            check(f.TryUserPlay(), "Native failure fixture could not request Play");
            await Until(() => f.Session is not null && (string)f.Session.GetType().GetField("Operation")!.GetValue(f.Session)! == "await-native-follow-reference",
                "Native failure fixture did not reach its barrier");
            var diagnostics = (SimulationFollowDiagnostics)f.Session!.GetType().GetField("Diagnostics")!.GetValue(f.Session)!;
            if (failure == "owner") { f.Snapshot["motion"]!["owner"] = "another-owner"; f.PublishNativeAdmissionReference(); }
            if (failure == "stop") await f.StopEvent();
            await Until(() => { f.Refresh(); return f.Session is null; }, "Native admission failure did not retire its static producers: " + failure);
            check(f.Mode == "pause" && !f.EngineRequests.Any(request => request.Action == "activate-start") &&
                f.Rpc.Requests.Count(request => request.Body["action"]?.GetValue<string>() == "follow-start") == 1,
                "Missing or changed native admission must fail closed without playing, retrying admission or replaying an old reference: " + failure);
            if (failure == "stop") check(diagnostics.FirstReason.Length == 0, "Operator Stop at the native admission barrier became a false first failure");
            if (failure == "timeout") check(diagnostics.FirstReason.Contains("post-acknowledgement"), "Native admission wait lost its bounded deadline diagnosis");
            if (failure == "timeout") check(f.Rpc.Requests.Any(request => request.Body["action"]?.GetValue<string>() == "hold" &&
                request.Body["request_id"]?.GetValue<long>() == 77),
                "A cached older idle request must not suppress the scoped hold for the newly acknowledged native admission");
        }
    }

    public static async Task RunPreparingReadRetry(Action<bool, string> check)
    {
        static async Task Until(Func<bool> predicate, string reason)
        {
            long began = Stopwatch.GetTimestamp();
            while (!predicate()) { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5) throw new InvalidOperationException(reason); await Task.Delay(5); }
        }
        await using (var f = new Fixture())
        {
            f.PreparingReadTimeouts = 1;
            await f.Start();
            await f.PinnedPreparingRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
            object session = f.Session!;
            check((string)session.GetType().GetField("RunId")!.GetValue(session)! == "run-a" && !f.Ready && f.Mode == "pause" && f.Rpc.Requests.IsEmpty,
                "The preparation retry fixture must already pin a paused run without any hardware admission");
            await Until(() => { f.Refresh(); return f.Ready || f.Session is null; }, "A pinned preparation did not recover after its read timeout");
            check(ReferenceEquals(f.Session, session) && f.Ready && f.Mode == "pause" && f.Rpc.Requests.IsEmpty &&
                f.EngineRequests.Count(request => request.Action == "prepare-start") == 1,
                "A150ms read timeout after pinning RunId must retain the exact preparation and reach Ready without moving or powering hardware: " +
                f.Get<string>("simulationDetail") + " · ready=" + f.Ready + " · RPC=" + f.Rpc.Requests.Count + " · mode=" + f.Mode);
        }
        await using (var f = new Fixture())
        {
            f.PreparingReadTimeouts = 1;
            await f.Start(); await f.PinnedPreparingRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Snapshot["devices"]![0]!["generation"] = 8L; f.Refresh();
            await Until(() => { f.Refresh(); return f.Session is null; }, "A changed CAN identity survived a pinned preparation read retry");
            check(f.Rpc.Requests.IsEmpty && !f.EngineRequests.Any(request => request.Action == "mark-start-ready"),
                "Preparing read retries must still validate the original device generation before any matching or Ready admission");
        }
    }

    public static async Task RunOrderedReadRetry(Action<bool, string> check)
    {
        static async Task Until(Func<bool> predicate, string reason)
        {
            long began = Stopwatch.GetTimestamp();
            while (!predicate()) { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5) throw new InvalidOperationException(reason); await Task.Delay(5); }
        }
        await using (var f = new Fixture())
        {
            await f.Start(); await Until(() => f.Ready, "Read retry fixture did not become Ready");
            check(f.TryUserPlay(), "Read retry fixture could not request Play");
            await Until(() => f.PlayStarted, "Read retry fixture did not activate");
            f.LoseNextOrderedReply = true;
            await f.OrderedReplyLost.Task.WaitAsync(TimeSpan.FromSeconds(3));
            object session = f.Session!;
            long validated = (long)session.GetType().GetField("OrderedEnvelopeTicks")!.GetValue(session)!;
            int completedReads = f.CompletedOrderedReads;
            int updates = f.Rpc.Requests.Count(request => request.Route == "/api/pika/follow-update");
            await Task.Delay(80);
            check(ReferenceEquals(f.Session, session) && f.Mode == "play" &&
                (long)session.GetType().GetField("OrderedEnvelopeTicks")!.GetValue(session)! == validated &&
                f.Rpc.Requests.Count(request => request.Route == "/api/pika/follow-update") > updates,
                "A delayed source read must keep the independent publisher alive without renewing its original validated envelope");
            await Until(() => f.CompletedOrderedReads > completedReads || f.Session is null, "The retained source reply was never recovered");
            var retried = f.EngineRequests.Where(request => request.Method == "robot.runtime.frames" && request.Action != "start" &&
                request.Body["afterFrameCursor"]!.GetValue<string>() == f.LostReplyCursor).ToArray();
            check(ReferenceEquals(f.Session, session) && f.Mode == "play" && retried.Length >= 2 &&
                (long)session.GetType().GetField("OrderedEnvelopeTicks")!.GetValue(session)! > validated &&
                f.Rpc.Requests.Count(request => request.Route == "/api/pika/follow-start") == 1 &&
                f.Rpc.Requests.Count(request => request.Body["action"]?.GetValue<string>() == "follow-start") == 1,
                "A150ms lost read reply must replay the same cursor without skipping source nodes, restarting leases or pausing healthy following");
        }
        await using (var f = new Fixture())
        {
            await f.Start(); await Until(() => f.Ready, "Cancelled read fixture did not become Ready");
            check(f.TryUserPlay(), "Cancelled read fixture could not request Play");
            await Until(() => f.PlayStarted, "Cancelled read fixture did not activate");
            f.LoseNextOrderedReply = true;
            await f.OrderedReplyLost.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var diagnostics = (SimulationFollowDiagnostics)f.Session!.GetType().GetField("Diagnostics")!.GetValue(f.Session)!;
            await f.StopEvent();
            await Until(() => f.Session is null, "Stop during a lost source reply did not retire its producers");
            int reads = f.EngineRequests.Count(request => request.Method == "robot.runtime.frames");
            await Task.Delay(180);
            check(f.EngineRequests.Count(request => request.Method == "robot.runtime.frames") == reads && f.Mode == "pause" &&
                f.Rpc.Requests.Count(request => request.Body["action"]?.GetValue<string>() == "follow-start") == 1,
                "Operator Stop must prevent retries and automatic motion admission after a cancelled source reply");
            check(diagnostics.FirstReason.Length == 0 && !File.Exists(diagnostics.Pathname) &&
                !f.Get<string>("simulationDetail").Contains("task was canceled", StringComparison.OrdinalIgnoreCase),
                "Normal Stop during an outstanding source read must not create a false diagnostic or cancellation tooltip");
        }
    }

    public static async Task RunReleaseRetirement(Action<bool, string> check)
    {
        static async Task Until(Func<bool> predicate)
        {
            long began = Stopwatch.GetTimestamp();
            while (!predicate()) { if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5) throw new InvalidOperationException("Release regression did not retire"); await Task.Delay(10); }
        }
        await using (var f = new Fixture())
        {
            await f.Start(); await Until(() => f.Ready); check(f.TryUserPlay(), "Release fixture could not request Play");
            try { await Until(() => f.PlayStarted); }
            catch (InvalidOperationException error) { throw new InvalidOperationException(error.Message + " · " + f.Get<string>("simulationDetail"), error); }
            f.Rpc.RejectIdleHold = true; f.FreezeTelemetry = true;
            f.Snapshot["devices"]![0]!["status"]!["stationary_s"] = 0d;
            Task stopped = f.StopEvent();
            await Until(() => f.Rpc.Requests.Any(r => r.Body["action"]?.GetValue<string>() == "hold"));
            await Task.Delay(100);
            check(f.Session is not null && f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "hold") == 1,
                "A fresh pre-hold active receipt caused a duplicate idle hold before independent post-ACK evidence");
            f.SetDetail("stopping; an outstanding request is still retiring");
            f.FreezeTelemetry = false; f.Snapshot["motion"]!["active"] = true; f.Refresh();
            await Task.Delay(80);
            check(f.Session is not null && f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "hold") == 1,
                "The first post-ACK active envelope ended the bounded wait before a complete idle proof");
            f.Snapshot["motion"]!["active"] = false; f.Refresh();
            await stopped; await Until(() => f.Session is null);
            check(f.Rpc.Requests.Count(r => r.Body["action"]?.GetValue<string>() == "hold") == 1 &&
                !f.Get<string>("simulationDetail").Contains("retiring") && !f.Get<string>("simulationDetail").Contains("release acknowledgement"),
                "Successful post-ACK native retirement sent an invalid idle hold or retained its old waiting hint");
        }
        foreach (bool lostAcknowledgement in new[] { false, true })
        {
            await using var f = new Fixture(mismatch: true);
            f.Rpc.DelayedOperation = "pose"; f.Rpc.LostAcknowledgement = lostAcknowledgement;
            await f.Start(); await f.Rpc.DelayedReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await f.StopEvent();
            f.FreezeTelemetry = true; f.Rpc.ReleaseDelayed.TrySetResult();
            await Until(() => f.Session is null);
            var events = f.Rpc.Events.ToArray(); int admitted = Array.FindIndex(events, e => e == "accepted:pose");
            check(admitted >= 0 && events.Skip(admitted + 1).Contains("send:hold"),
                "A stale post-first-hold receipt skipped the final barrier after late pose admission");
        }
        await using (var f = new Fixture())
        {
            f.Mode = "pause"; bool rejected = false;
            try { await f.Start(); } catch (InvalidOperationException) { rejected = true; }
            await Until(() => f.Session is null);
            check(rejected && !f.Get<string>("simulationDetail").Contains("still retiring"), "Failed setup retained an inactive retirement hint");
            f.Mode = "edit"; await f.Start(); await Until(() => f.Ready);
            check(f.Get<string>("message").StartsWith("Simulation ready") && !f.Get<string>("simulationDetail").Contains("Stop Play first"),
                "A successful new preparation retained the previous setup error");
        }
    }

    public static async Task RunZeroSpeed(Action<bool, string> check)
    {
        await using var fixture = new Fixture();
        fixture.SetSpeed(0);
        bool rejected = false;
        try { await fixture.Start(); } catch (InvalidOperationException error) { rejected = error.Message.Contains("above 0%", StringComparison.Ordinal); }
        check(rejected && fixture.Session is null && fixture.Rpc.Requests.IsEmpty && !fixture.EngineRequests.Any(request => request.Action == "prepare-start"),
            "Zero speed was silently clamped or prepared/admitted before asking for a nonzero setting");
    }

    private static async Task RunTrajectoryTransport(Action<bool, string> check)
    {
        foreach (bool wrongAcknowledgement in new[] { false, true })
        {
            await using var fixture = new Fixture();
            fixture.SetTransport(SimulationTrajectoryTransport.StreamedPosition);
            fixture.Snapshot["streamed_position_transport_supported"] = true;
            fixture.Snapshot["streamed_position_wire_validation_supported"] = true;
            fixture.Snapshot["devices"]![0]!["streamed_position_supported"] = true;
            fixture.Snapshot["devices"]![0]!["hardware"] = "H-V1.2-1";
            fixture.Snapshot["devices"]![0]!["firmware"] = "S-V1.9-0";
            if (wrongAcknowledgement) fixture.Rpc.TransportAcknowledgementOverride = SimulationTrajectoryTransport.PositionVelocity;
            fixture.Refresh();
            await fixture.Start();
            await Until(() => fixture.Ready, "Explicit transport fixture did not reach Ready");
            // The preparation pins the selected transport; a later preference
            // edit cannot turn this admission into another wire-mode request.
            fixture.SetTransport(SimulationTrajectoryTransport.PositionVelocity);
            check(fixture.TryUserPlay(), "Explicit transport preparation could not request Play");
            await Until(() => wrongAcknowledgement ? fixture.Session is null : fixture.PlayStarted,
                "Explicit transport admission did not complete or retire");
            var start = fixture.Rpc.Requests.Single(r => r.Body["action"]?.GetValue<string>() == "follow-start");
            check(start.Body["trajectory"]?.GetValue<bool>() == true && start.Body["trajectory_transport"]?.GetValue<string>() == SimulationTrajectoryTransport.StreamedPosition,
                "The prepared streamed-position request changed transport before native admission");
            check(wrongAcknowledgement ? fixture.Mode == "pause" && !fixture.Rpc.Requests.Any(r => r.Route == "/api/pika/follow-start") : fixture.PlayStarted,
                "A wrong native transport acknowledgement released Play or admitted the independent gripper");
        }
        async Task Until(Func<bool> predicate, string reason)
        {
            long began = Stopwatch.GetTimestamp();
            while (!predicate())
            {
                if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5) throw new InvalidOperationException(reason);
                await Task.Delay(10);
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly PipersPlugin plugin = new();
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
        public volatile bool DelaySetup;
        public volatile bool RuntimeReadTimeout;
        public volatile bool DelayNativeAdmissionTelemetry;
        private long pendingNativeSequence;
        private double[] pendingNativeCommand = new double[6];
        public int PreparingReadTimeouts;
        public TaskCompletionSource PinnedPreparingRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool LoseNextOrderedReply;
        public int CompletedOrderedReads;
        public string LostReplyCursor = "";
        public TaskCompletionSource OrderedReplyLost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool StalePikaAfterRead;
        public volatile bool ChangeActivationFrame;
        public bool PreparationSupported = true, ActivationSupported = true, AutoPrepare = true, FramesSupported = true;
        public volatile bool FrameCursorGap, TerminalFrame;
        public bool ScriptedMissedGrasp, ScriptedSourceMiss;
        public bool GroupMode;
        private bool groupArmed;
        private readonly CancellationTokenSource groupTelemetryStop = new();
        private Task? groupTelemetryWorker;
        public string EpisodeId = "";
        public double FrameSourceAgeMs;
        public ulong SealedFrameCursor;
        public volatile int PikaEvidenceMode;
        private double pikaRequestAt;
        private volatile bool readyGate;
        private string prepareId = "";
        private readonly ConcurrentDictionary<string, byte> canceledPreparations = new();
        private double simulationTime;
        private ulong frameCursor;
        private readonly List<JsonObject> sourceFrames = [];
        public bool Ready => readyGate;
        private volatile bool startRequested, activated;
        public bool PlayStarted => Session is { } current && (bool)current.GetType().GetField("PlayStarted")!.GetValue(current)!;
        public bool TryUserPlay() { if (!readyGate || Mode != "pause") return false; startRequested = true; return true; }
        public TaskCompletionSource SetupReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSetup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RuntimeReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRuntime { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private double scale = 1;
        public double Scale { get => Volatile.Read(ref scale); set => Volatile.Write(ref scale, value); }
        private long sequence;
        public double EnvelopeAgeMs;
        public bool FreezeTelemetry;
        public int LinkEditorDelayMilliseconds;
        public string PrimaryReceipt => Path.Combine(settingsDirectory, "diagnostics", "primary-action", "latest.json");
        public object? Session => Get<object?>("simulationSession");
        public bool GroupActive => Get<object?>("groupSimulation") is not null;
        public string GroupStage => Get<object?>("groupSimulation")?.GetType().GetField("Stage")!.GetValue(Get<object?>("groupSimulation")) as string ?? "";
        public async Task GroupEvent(string action)
        { await (Task)typeof(PipersPlugin).GetMethod("GroupFollowEvent", Fields)!.Invoke(plugin, [action, CancellationToken.None])!; }
        public Task ClickSidebarGroupStop()
        {
            var sidebar = (Widget)typeof(PipersPlugin).GetMethod("GroupFollowControls", Fields)!.Invoke(plugin, null)!;
            var stop = sidebar.Children!.Single(w => w.Text == "Stop following" && w.Visible != false);
            string wire = WorkspaceEventScope.Wrap(Get<long>("workspaceViewEpoch"), "piper-1", stop.Id);
            return plugin.HandleEventAsync(new("fixture", wire, "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
        }
        public void RequestGroupPlay() { if (!groupArmed || Mode != "edit") throw new InvalidOperationException("Group is not armed"); startRequested = true; Mode = "pause"; }
        public void FocusGroupSecond()
        { Set("preferences", RobotProfileRouting.Activate(Get<Preferences>("preferences"), "piper-2")); Snapshot["selected"] = "can-b"; Refresh(); }
        public void ConfigureGroup()
        {
            GroupMode = true; EpisodeId = "episode-group";
            var profiles = new[] { new RobotProfile("piper-1", "First", "can-a", Speed: 10, RobotId: "robot-a", PikaDevice: "/dev/pika-a", FollowSimulationSelected: true),
                new RobotProfile("piper-2", "Second", "can-b", Speed: 10, RobotId: "robot-a", PikaDevice: "/dev/pika-b", RobotJointPrefix: "piper3_", FollowSimulationSelected: true) };
            Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots: profiles, ActiveRobotId: "piper-1")));
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
            Type approval = typeof(PipersPlugin).GetNestedType("ProfileApproval", BindingFlags.NonPublic)!;
            var approvals = Get<object>("controlApprovals");
            for (int i = 0; i < profiles.Length; i++)
            {
                var p = profiles[i];
                var value = Activator.CreateInstance(approval, "fixture-backend", p.CanDeviceId, i == 0 ? 7L : 8L, p.RobotId, p.RobotJointPrefix, p.PikaDevice, p.WslDistribution)!;
                approvals.GetType().GetMethod("TryAdd")!.Invoke(approvals, [p.Id, value]);
            }
            Rpc.Accepted = (route, body) =>
            {
                var device = Snapshot["devices"]!.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == body["device_id"]?.GetValue<string>());
                string action = body["action"]?.GetValue<string>() ?? "";
                if (device is not null && route == "/api/command")
                {
                    var motion = device["motion"]!;
                    if (action == "follow-start") { motion["active"] = true; motion["following"] = true; motion["trajectory_follow"] = true; motion["request_id"] = 77L; motion["follow_sequence"] = 0L; }
                    if (action == "follow-update")
                    {
                        motion["follow_sequence"] = body["sequence"]!.DeepClone();
                        for (int i = 0; i < 6; i++) { device["joints"]![i]!["command_deg"] = body["degrees"]![i]!.DeepClone(); device["joints"]![i]!["actual_deg"] = body["degrees"]![i]!.DeepClone(); }
                    }
                    if (action == "hold") { motion["active"] = false; motion["following"] = false; }
                }
                var pika = Snapshot["pikas"]!.AsArray().OfType<JsonObject>().SingleOrDefault(p => p["device"]?.GetValue<string>() == body["pika_device"]?.GetValue<string>());
                if (pika is not null && route == "/api/pika/follow-update") pika["telemetry"]!["motor"]!["Position"] = PikaProtocol.Angle(body["opening_mm"]!.GetValue<double>());
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
        public bool Ending => Session is { } current && (int)current.GetType().GetField("Ending")!.GetValue(current)! != 0;
        public SemaphoreSlim Commands => Get<SemaphoreSlim>("commands");
        public T Get<T>(string field) => (T)typeof(PipersPlugin).GetField(field, Fields)!.GetValue(plugin)!;
        public void SetSpeed(double speed) => Set("preferences", Get<Preferences>("preferences") with { Speed = speed });
        public void SetApproved(bool value) => Set("approved", value);
        public void ClearHeaderAndRecordLateFailure(object session)
        {
            Set("uiActionError", "");
            typeof(PipersPlugin).GetMethod("RecordSimulationFailure", Fields)!
                .Invoke(plugin, [session, new InvalidOperationException("late hold cleanup"), null]);
        }
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
                device["motion"]!["request_id"] = 77L; device["motion"]!["active"] = true;
                device["motion"]!["phase"] = "teaching-return";
                device["status"]!["control"] = 1; device["teaching"]!["phase"] = "off";
                device["teaching"]!["return_required"] = false;
                if (completeImmediately) CompleteResume(); else Refresh();
            };
            Refresh();
        }
        public void SetResumeEligible()
        { Snapshot["devices"]![0]!["teaching"]!["can_return"] = true; Refresh(); }
        public void CompleteResume()
        {
            var device = Snapshot["devices"]![0]!;
            device["motion"]!["active"] = false; device["motion"]!["phase"] = "complete";
            Refresh();
        }
        public void CompleteResumeOnHeartbeat()
        {
            var previous = Rpc.Accepted;
            Rpc.Accepted = (route, body) => { previous?.Invoke(route, body); if (route == "/api/heartbeat") CompleteResume(); };
            Rpc.RejectHeartbeatAfterAccepted = true;
        }
        public void InterruptResume(string kind)
        {
            var device = Snapshot["devices"]![0]!;
            if (kind == "permission") Set("targetPermissionEpoch", Get<long>("targetPermissionEpoch") + 1);
            if (kind == "generation") device["generation"] = 8L;
            if (kind == "playback") device["teaching"]!["phase"] = "playback";
            if (kind == "fault") device["joints"]![3]!["fault"] = 1;
        }
        public void SetTransport(string transport) => Set("preferences", Get<Preferences>("preferences") with { SimulationTransport = transport });
        private void Set(string field, object? value) => typeof(PipersPlugin).GetField(field, Fields)!.SetValue(plugin, value);

        public Fixture(bool mismatch = false)
        {
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
                        if (method == "scene.robots") return JsonSerializer.SerializeToElement(new JsonObject { ["sceneId"] = "source-a", ["robots"] = new JsonArray(
                            new JsonObject { ["entityId"] = "robot-a", ["modelAssetId"] = "asset-a", ["modelRevision"] = "model-a", ["joints"] = GroupPose()["joints"]!.DeepClone() }) });
                        if (method == "robot.subscribe") return JsonSerializer.SerializeToElement(new { sceneId = "source-a", entityId = "robot-a" });
                        if (method == "host.info") return JsonSerializer.SerializeToElement(new { robotRuntimeFramesSupported = FramesSupported, robotRuntimeTracksSupported = GroupMode });
                        if (method == "robot.runtime.read" && body["runId"]?.GetValue<string>() == "run-a" && Mode == "pause" && !readyGate &&
                            PreparingReadTimeouts > 0)
                        {
                            Interlocked.Decrement(ref PreparingReadTimeouts);
                            PinnedPreparingRead.TrySetResult();
                            await Task.Delay(Timeout.Infinite, cancellation);
                        }
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
                            if (action == "arm-next-play")
                            {
                                if (Mode != "edit") throw new InvalidOperationException("Arm-next-play requires Edit");
                                prepareId = nonce; groupArmed = true;
                                return JsonSerializer.SerializeToElement(new { applied = true, mode = Mode, prepareId, startArmedForNextPlay = true, runId = "" });
                            }
                            if (action == "prepare-start")
                            {
                                prepareId = nonce;
                                if (DelaySetup) { SetupReached.TrySetResult(); await ReleaseSetup.Task; }
                                if (!canceledPreparations.ContainsKey(nonce)) Mode = AutoPrepare ? "pause" : "preparing";
                            }
                            if (action == DelayedRuntimeAction) { RuntimeReached.TrySetResult(); await ReleaseRuntime.Task; }
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
                                if (nonce == prepareId) { readyGate = startRequested = false; if (Mode == "preparing") Mode = "edit"; else if (Mode != "edit") Mode = "pause"; }
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
                            foreach (var joint in pose["joints"]!.AsArray().OfType<JsonObject>().Where(j => int.Parse(j["name"]!.GetValue<string>()[5..]) <= 6))
                                joint["position"] = 0d;
                            foreach (var actuator in pose["actuators"]!.AsArray().OfType<JsonObject>().Where(a => a["name"]!.GetValue<string>() != "gripper"))
                                actuator["control"] = 0d;
                        }
                        pose["mode"] = Mode; pose["timeScale"] = scale;
                        pose["startPreparationSupported"] = PreparationSupported;
                        pose["startActivationSupported"] = ActivationSupported;
                        pose["startRequested"] = startRequested;
                        pose["startActivated"] = activated;
                        pose["prepareId"] = prepareId; pose["startReady"] = readyGate; pose["startMatchRequired"] = !readyGate;
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
                                foreach (var profile in Get<Preferences>("preferences").Robots!)
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
                        if (method == "robot.runtime.read" && StalePikaAfterRead) Snapshot["pika"]!["age_s"] = .3;
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
            for (int joint = 0; joint < 6; joint++) Snapshot["devices"]![0]!["joints"]![joint]!["command_deg"] = pendingNativeCommand[joint];
            Refresh();
        }

        public void Refresh()
        {
            if (FreezeTelemetry) return;
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            Snapshot["pika"]!["last_motor_request"] = PikaEvidenceMode == 1 ? "old-position" : "pika-position-fixture";
            Snapshot["pika"]!["request_time_s"] = pikaRequestAt;
            Snapshot["pika"]!["feedback_time_s"] = PikaEvidenceMode == 2 ? pikaRequestAt - 1 : PikaEvidenceMode == 4 ? pikaRequestAt + .0001 : now;
            Snapshot["pika"]!["pending"] = PikaEvidenceMode == 3;
            if (GroupMode) foreach (var pika in Snapshot["pikas"]!.AsArray().OfType<JsonObject>()) pika["feedback_time_s"] = now;
            Get<LatestTelemetry>("telemetry").Publish(new(Snapshot, "fixture-backend", Interlocked.Increment(ref sequence), Stopwatch.GetTimestamp(), EnvelopeAgeMs));
        }
        public string ReadinessBlocker() => (string)typeof(PipersPlugin).GetMethod("SimulationReadinessBlocker", Fields)!.Invoke(plugin,
            [Snapshot["devices"]![0]!.AsObject()])!;
        public Task Start() => Invoke("SimulationEvent", "toggle", CancellationToken.None);
        public void RemoveMeasuredLink(int editorDelayMilliseconds) { Set("link", null); LinkEditorDelayMilliseconds = editorDelayMilliseconds; }
        public void UseUiDispatcher() => Set("uiEvents", new UiEventDispatch((input, cancellation) => Invoke("HandleEventCoreAsync", input, cancellation),
            () => Get<long>("targetPermissionEpoch"), input => WorkspaceCommandPolicy.RequiresControls(WorkspaceEventScope.Unwrap(Get<long>("workspaceViewEpoch"), "piper-1", input.WidgetId)),
            error => throw new InvalidOperationException("Unexpected fixture UI actor failure", error)));
        public Task Navigate(string action) => Invoke("WorkspaceEvent", action, CancellationToken.None);
        public Task Click(string id)
        {
            Refresh();
            string wireId = WorkspaceEventScope.Wrap(Get<long>("workspaceViewEpoch"), "piper-1", id);
            return plugin.HandleEventAsync(new("fixture", wireId, "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
        }
        public void SetDetail(string detail) => Set("simulationDetail", detail);
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
            ReleaseSetup.TrySetResult();
            ReleaseRuntime.TrySetResult();
            if (GroupActive)
                await (Task)typeof(PipersPlugin).GetMethod("StopGroupSimulationFollowing", Fields, [typeof(CancellationToken)])!
                    .Invoke(plugin, [CancellationToken.None])!;
            groupTelemetryStop.Cancel(); if (groupTelemetryWorker is not null) await groupTelemetryWorker;
            if (Session is { } session)
            {
                try { await StopEvent().WaitAsync(TimeSpan.FromSeconds(4)); } catch (InvalidOperationException) { }
                // Stop may acknowledge before the final diagnostics are saved.
                // Join the real session lifetime before removing its storage.
                var ended = (TaskCompletionSource)session.GetType().GetField("Ended")!.GetValue(session)!;
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(7));
            }
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
        public bool LostAcknowledgement;
        public string? TransportAcknowledgementOverride;
        public bool HoldFailure;
        public bool RejectIdleHold;
        public Func<bool>? ArmActive;
        public bool StreamUnavailable, PikaHeartbeatUnavailable;
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
            string operation = command.Route == "/api/pika/follow-start" ? "pika-start" : command.Route == "/api/pika/position" ? "pika-position" : action == "follow-start" ? "arm-start" : action;
            Events.Enqueue("send:" + (command.Route == "/api/pika/hold" ? "pika-hold" : operation));
            async Task<TResponse> Reply()
            {
                if (RejectIdleHold && action == "hold" && ArmActive?.Invoke() != true)
                    return (TResponse)(object)new ControlReply { Status = 400, Json = "{\"error\":\"Confirm that the full motion path is clear and the arm is attended.\"}" };
                if (PikaHeartbeatUnavailable && command.Route == "/api/pika/heartbeat") throw new RpcException(new Status(StatusCode.Unavailable, "fixture transient transport"));
                if (HoldFailure && action == "hold")
                    return (TResponse)(object)new ControlReply { Status = 409, Json = "{\"error\":\"hold transport failure\"}" };
                if (UpdateFailure.Length > 0 && action == "follow-update")
                    return (TResponse)(object)new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = UpdateFailure }) };
                if (PikaFailureAudit is { } audit && command.Route == "/api/pika/follow-update")
                    return (TResponse)(object)new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = "No matching active Pika following session.", pika_follow_audit = audit }) };
                if (FailArmWithPikaAudit && PikaFailureAudit is not null && action == "follow-update")
                    return (TResponse)(object)new ControlReply { Status = 409, Json = "{\"error\":\"Arm following input expired.\"}" };
                if (DelayedOperation.Length > 0 && operation == DelayedOperation)
                {
                    AdmissionCancellation = options.CancellationToken; AdmissionDeadline = options.Deadline;
                    DelayedReached.TrySetResult(); await ReleaseDelayed.Task;
                    Events.Enqueue("accepted:" + operation);
                }
                Accepted?.Invoke(command.Route, body);
                if (RejectHeartbeatAfterAccepted && command.Route == "/api/heartbeat")
                    return (TResponse)(object)new ControlReply { Status = 409, Json = "{\"error\":\"No matching active motion lease\"}" };
                if (LostAcknowledgement && operation == DelayedOperation) throw new RpcException(new Status(StatusCode.Cancelled, "Accepted fixture operation lost its acknowledgement"));
                var response = new JsonObject { ["request_id"] = 77, ["motor_request_id"] = "pika-position-fixture", ["follow_session"] = "pika-fixture", ["trajectory_follow"] = true };
                if (action == "follow-start" && body["trajectory_transport"] is not null)
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

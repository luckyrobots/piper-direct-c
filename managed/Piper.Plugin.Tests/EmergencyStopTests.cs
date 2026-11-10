using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

internal static class EmergencyStopTests
{
    public static async Task Run()
    {
        foreach (string invalidation in new[] { "cancel", "stop", "profile", "generation" })
        {
            using var fixture = new Fixture();
            await fixture.Plugin.Render(CancellationToken.None);
            string Wire(string action) => WorkspaceEventScope.Wrap(fixture.Plugin.workspace.Epoch, "piper-1", action);
            await fixture.Event(Wire("safety.accept"), default);
            Check(!fixture.Plugin.approved && fixture.Rpc.Routes.IsEmpty,
                "An Enable acknowledgement without a presented prompt reached the hardware transport");
            await fixture.Event(Wire("enable"), default);
            var opened = fixture.Authority.Current.Confirmation;
            string oldAccept = Wire("safety.accept");
            Check(opened is not null && !fixture.Plugin.approved && fixture.Rpc.Routes.IsEmpty,
                "Opening a clearance prompt approved or sent an action");
            switch (invalidation)
            {
                case "cancel":
                    await fixture.Event(Wire("safety.cancel"), default);
                    break;
                case "stop":
                    await fixture.Event("robot.piper-1.stop", default);
                    break;
                case "profile":
                    var preferences = fixture.Plugin.preferences;
                    var second = new RobotProfile("piper-2", "Second", "can-two");
                    fixture.Plugin.preferences = RobotProfileRouting.Activate(preferences with { Robots = [.. preferences.Robots!, second] }, second.Id);
                    await fixture.Plugin.Render(CancellationToken.None);
                    break;
                case "generation":
                    const string backend = "fake-backend-session";
                    var telemetry = fixture.Plugin.telemetry;
                    var first = telemetry.Latest!;
                    var next = first.State.DeepClone().AsObject();
                    next["devices"]![0]!["generation"] = 2L;
                    var replacementReceipt = first with { State = next, Sequence = 2 };
                    fixture.Plugin.telemetry.AdoptScope(new TelemetryScope(backend, "can-one", 1));
                    fixture.Plugin.ApplyIncomingScope(replacementReceipt);
                    fixture.Plugin.telemetry.PublishState(next);
                    telemetry.Publish(replacementReceipt);
                    break;
            }
            await fixture.Event(oldAccept, default);
            Check(fixture.Authority.Current.Confirmation is null && !fixture.Plugin.approved &&
                fixture.Rpc.Routes.All(route => route == "/api/stop/coordinated"),
                "An old workspace confirmation survived " + invalidation);
            if (invalidation != "cancel") continue;
            await fixture.Event(Wire("enable"), default);
            var reopened = fixture.Authority.Current.Confirmation;
            await fixture.Event(oldAccept, default);
            Check(reopened is not null && !ReferenceEquals(opened, reopened) &&
                ReferenceEquals(reopened, fixture.Authority.Current.Confirmation) && !fixture.Plugin.approved && fixture.Rpc.Routes.IsEmpty,
                "A delayed Accept consumed a newly opened prompt");
        }

        var authority = new RobotCommandAuthority();
        var original = new RobotCommandScope("piper-1", "original-backend", "can-one", 1);
        long originalEpoch = authority.Epoch;
        Check(authority.TryAcknowledge(originalEpoch, original, 17), "A current acknowledgement could not publish its lease");
        var previous = authority.Current.Lease!;
        long nextEpoch = authority.Revoke();
        var replacement = original with { Backend = "replacement-backend", Generation = 2 };
        Check(!authority.TryAcknowledge(originalEpoch, original, 17) && authority.Current is { Lease: null, LastCommand: null },
            "An acknowledgement for a retired epoch republished a command");
        Check(authority.TryAcknowledge(nextEpoch, replacement, 17), "A new scoped request could not reuse the controller's request number");
        authority.EndLease(previous);
        authority.EndStatus(previous);
        Check(authority.Current.Lease?.Scope == replacement && authority.Current.LastCommand?.Scope == replacement,
            "Retiring the old command cleared a different connection's reused request number");
        var approved = new FollowControlApproval(replacement.Backend, replacement.Device, replacement.Generation, "sim", "", "pika-a", "");
        var partner = approved with { Device = "can-two", PikaDevice = "pika-b" };
        Check(authority.TryOpenConfirmation(nextEpoch, "piper-1", approved), "Current authority could not open a workspace confirmation");
        var confirmation = authority.Current.Confirmation!;
        int activations = 0;
        Check(!authority.TryConsumeConfirmation(confirmation, "piper-2", partner, () => activations++) &&
            authority.TryConsumeConfirmation(confirmation, "piper-1", approved, () => activations++) &&
            !authority.TryConsumeConfirmation(confirmation, "piper-1", approved, () => activations++) && activations == 1,
            "Workspace clearance was accepted for another scope or consumed more than once");
        Check(authority.TrySaveApproval(nextEpoch, "piper-1", approved) && authority.TrySaveApproval(nextEpoch, "piper-2", partner),
            "Approvals for independent robots could not coexist");
        authority.Revoke("piper-1");
        bool restored = false;
        Check(!authority.TrySaveApproval(nextEpoch, "piper-1", approved) &&
            !authority.TryRestoreApproval(authority.Epoch, "piper-1", approved, () => restored = true) &&
            !restored && authority.Current.Approvals.TryGetValue("piper-2", out var retained) && retained == partner,
            "A revoked robot approval returned, or revocation removed a different robot's approval");

        using (var fixture = new Fixture())
        {
            const string backend = "fake-backend-session";
            var telemetry = fixture.Plugin.telemetry;
            var first = telemetry.Latest!;
            var profile = RobotProfileRouting.Active(fixture.Plugin.preferences);
            fixture.Plugin.telemetry.AdoptScope(new TelemetryScope(backend, "can-one", 1));
            fixture.Authority.TrySaveApproval(fixture.Authority.Epoch, profile.Id,
                new(backend, "can-one", 1, profile.RobotId, profile.RobotJointPrefix, profile.PikaDevice, profile.WslDistribution));
            fixture.Plugin.approved = true;
            var next = first.State.DeepClone().AsObject();
            next["devices"]![0]!["generation"] = 2L;
            var replacementReceipt = first with { State = next, Sequence = 2 };
            fixture.Plugin.ApplyIncomingScope(replacementReceipt);
            fixture.Plugin.RestoreControlApproval();
            Check(telemetry.Latest?.Connected == false && !fixture.Plugin.approved,
                "Old measured evidence restored approval between scope revocation and telemetry publication");
            fixture.Plugin.telemetry.PublishState(next);
            telemetry.Publish(replacementReceipt);
            fixture.Plugin.RestoreControlApproval();
            Check(!fixture.Plugin.approved && fixture.Authority.Current.Approvals.Count == 0,
                "The replacement CAN generation inherited the old approval");
        }

        using (var fixture = new Fixture())
        {
            await fixture.Plugin.Render(CancellationToken.None);
            var preferences = fixture.Plugin.preferences;
            var captured = RobotProfileRouting.Active(preferences);
            var second = new RobotProfile("piper-2", "Second", "can-two", PikaDevice: "/dev/pika-two");
            fixture.Plugin.preferences = RobotProfileRouting.Activate(preferences with { Robots = [captured, second] }, second.Id);
            await fixture.Event("robot.piper-1.stop", default);
            var body = JsonNode.Parse(fixture.Rpc.RequestBodies.Single());
            Check(fixture.Rpc.Routes.SequenceEqual(new[] { "/api/stop/coordinated" }) &&
                body?["device_id"]?.GetValue<string>() == "can-one" && body?["pika_device"]?.GetValue<string>() == "/dev/pika-one",
                "STOP mixed captured arm/gripper identity with a newly selected profile");
        }

        using (var fixture = new Fixture())
        {
            await fixture.Commands.WaitAsync();
            long before = fixture.Authority.Epoch;
            fixture.Plugin.approved = true;
            fixture.Authority.TryAcknowledge(before, new("piper-1", "fake-backend-session", "can-one", 1), 17);
            using var cancelledCaller = new CancellationTokenSource(); cancelledCaller.Cancel();
            try
            {
                await fixture.Event("robot.piper-1.stop", cancelledCaller.Token).WaitAsync(TimeSpan.FromSeconds(1));
                Check(fixture.Commands.CurrentCount == 0 && fixture.Rpc.Routes.SequenceEqual(new[] { "/api/stop/coordinated" }),
                    "Priority STOP waited for or released the ordinary UI command semaphore");
                Check(fixture.Rpc.RequestTokensWereLive && fixture.Authority.Epoch > before &&
                    !fixture.Plugin.approved && fixture.Authority.Current.Lease is null,
                    "Caller cancellation or an old permission/lease defeated a requested STOP");
                Check(fixture.Plugin.message.Contains("Arm STOP acknowledged") && fixture.Plugin.message.Contains("Pika hold unverified"),
                    "Partial coordinated STOP reported failure of one channel as failure of both, or claimed an unverified gripper hold");
            }
            finally { fixture.Commands.Release(); }
        }

        foreach (string wire in new[] { "robot.other.stop", "stop", "view.999.robot.piper-1.stop" })
        {
            using var fixture = new Fixture();
            await fixture.Commands.WaitAsync();
            long before = fixture.Authority.Epoch;
            Task foreign = fixture.Event(wire, default);
            await Task.Delay(20);
            Check(fixture.Rpc.Routes.IsEmpty && fixture.Authority.Epoch == before,
                "A foreign, unscoped or malformed STOP entered the priority hardware path: " + wire);
            fixture.Commands.Release();
            await foreign.WaitAsync(TimeSpan.FromSeconds(1));
            Check(fixture.Rpc.Routes.IsEmpty, "A rejected STOP reached transport after the UI gate became available");
        }

        using (var fixture = new Fixture())
        {
            fixture.Plugin.approved = true;
            fixture.Rpc.DelayCommand = true;
            Task pending = fixture.Plugin.Command("jog", new JsonObject { ["joint"] = 6, ["delta_deg"] = 1.0 },
                CancellationToken.None, CommandPurpose.Control, null, null, null);
            try
            {
                await fixture.Rpc.CommandReached.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await fixture.Event("robot.piper-1.stop", default).WaitAsync(TimeSpan.FromSeconds(1));
                fixture.Rpc.CommandReply.TrySetResult(new ControlReply { Status = 202, Json = "{\"request_id\":17}" });
                bool rejected = false;
                try { await pending; } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && fixture.Authority.Current is { Lease: null, LastCommand: null } &&
                    fixture.Rpc.Routes.SequenceEqual(new[] { "/api/command", "/api/stop/coordinated" }),
                    "A motion acknowledgement received after priority STOP revived its local lease or retried the command");
            }
            finally { fixture.Rpc.CommandReply.TrySetCanceled(); }
        }

        using (var fixture = new Fixture())
        {
            fixture.Rpc.DelayConnect = true;
            fixture.Plugin.approved = true;
            await fixture.Commands.WaitAsync();
            Task enable = fixture.Plugin.Enable(CancellationToken.None, true, null, null);
            try
            {
                await fixture.Rpc.ConnectReached.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await fixture.Event("robot.piper-1.stop", default).WaitAsync(TimeSpan.FromSeconds(1));
                fixture.Rpc.ConnectReply.TrySetResult(new ControlReply { Status = 200, Json = "{\"ok\":true}" });
                bool rejected = false;
                try { await enable; } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && !fixture.Rpc.Routes.Contains("/api/pika/enable") && !fixture.Plugin.approved,
                    "A delayed Enable continuation powered Pika after priority STOP invalidated its permission epoch");
                Check(fixture.Rpc.Routes.Contains("/api/stop/coordinated"), "Blocked Enable suppressed the real priority STOP request");
            }
            finally
            {
                fixture.Rpc.ConnectReply.TrySetResult(new ControlReply { Status = 200, Json = "{\"ok\":true}" });
                fixture.Commands.Release();
            }
        }

        using (var fixture = new Fixture())
        {
            fixture.Rpc.CoordinatedMissing = true;
            await fixture.Event("robot.piper-1.stop", default).WaitAsync(TimeSpan.FromSeconds(1));
            Check(fixture.Rpc.Routes.SequenceEqual(new[] { "/api/stop/coordinated", "/api/stop" }),
                "Missing coordinated-stop support prevented the independent arm STOP fallback");
            Check(fixture.Plugin.message.Contains("Arm STOP acknowledged") && fixture.Plugin.message.Contains("gripper state unverified"),
                "Legacy fallback claimed a verified Pika hold");
        }
        foreach (string change in new[] { "physical STOP", "hand guiding" })
        {
            using var fixture = new Fixture();
            fixture.Rpc.DelayConnect = true;
            await fixture.Plugin.Render(CancellationToken.None);
            string Wire(string action) => WorkspaceEventScope.Wrap(fixture.Plugin.workspace.Epoch, "piper-1", action);
            await fixture.Event(Wire("enable"), default);
            Task accept = fixture.Event(Wire("safety.accept"), default);
            try
            {
                await fixture.Rpc.ConnectReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var telemetry = fixture.Plugin.telemetry;
                var receipt = telemetry.Latest!;
                var changed = receipt.State.DeepClone().AsObject();
                var status = changed["devices"]![0]!["status"]!;
                if (change == "physical STOP") status["arm_status"] = 1;
                else status["teach_state"] = 1;
                var newReceipt = receipt with { State = changed, Sequence = 2 };
                fixture.Plugin.ApplyIncomingScope(newReceipt);
                telemetry.Publish(newReceipt);
                fixture.Plugin.telemetry.PublishState(changed);
                fixture.Rpc.ConnectReply.TrySetResult(new ControlReply { Status = 200, Json = "{\"ok\":true}" });
                await accept.WaitAsync(TimeSpan.FromSeconds(2));
                Check(fixture.Rpc.Routes.SequenceEqual(new[] { "/api/pika/connect" }) &&
                    fixture.Authority.Current.Confirmation is null && fixture.Authority.Current.Approvals.Count == 0,
                    "A delayed Enable powered Pika after fresh feedback showed " + change);
            }
            finally { fixture.Rpc.ConnectReply.TrySetCanceled(); }
        }
        using (var fixture = new Fixture())
        {
            var telemetry = fixture.Plugin.telemetry;
            var receipt = telemetry.Latest!;
            var snapshot = receipt.State.DeepClone().AsObject();
            var gripper = snapshot["pika"]!.AsObject();
            gripper["aliases"] = new JsonArray("/dev/pika-one");
            gripper["device"] = "/dev/pika/by-host/fixture";
            snapshot["pikas"] = new JsonArray(gripper.DeepClone());
            fixture.Plugin.telemetry.PublishState(snapshot);
            telemetry.Publish(receipt with { State = snapshot, Sequence = 2 });
            fixture.Plugin.approved = true;
            fixture.Plugin.telemetry.Pika.Enter(PikaSessionRole.Owned);
            await fixture.Plugin.Enable(CancellationToken.None, true, null, null);
            Check(fixture.Rpc.Routes.IsEmpty && fixture.Authority.Current.Approvals.Count == 1,
                "An assigned Pika alias was found in telemetry but Enable reconfigured the already-owned canonical gripper");
        }
        {
            // Deterministic interleavings: a STOP lands between a gesture writer's
            // observation and its transition. STOP must win every time.
            var plugin = new PipersPlugin();
            var teleoperating = new ViewportDrag(ViewportDragPhase.Teleoperating, "joint3");
            plugin.viewportDrag = teleoperating;
            plugin.StopViewportDrag();
            Check(!plugin.TryBeginViewportFollowing(teleoperating, 7, "joint3") && plugin.viewportDrag.Phase == ViewportDragPhase.Off,
                "A follow-start acknowledgement observed before STOP revived Following after it");
            var following = new ViewportDrag(ViewportDragPhase.Following, "joint3", Request: 7);
            plugin.viewportDrag = following;
            plugin.StopViewportDrag();
            plugin.EndViewportGesture(following);
            Check(plugin.viewportDrag is { Phase: ViewportDragPhase.Off, Joint: "joint3" },
                "A gesture end observed before STOP turned Off back into Teleoperating");
            plugin.viewportDrag = teleoperating;
            Check(plugin.TryBeginViewportFollowing(teleoperating, 7, "joint4") &&
                plugin.viewportDrag is { Phase: ViewportDragPhase.Following, Request: 7, FollowJoint: "joint4" },
                "An undisturbed follow-start did not enter Following");
            plugin.EndViewportGesture(plugin.viewportDrag);
            Check(plugin.viewportDrag is { Phase: ViewportDragPhase.Teleoperating, Request: 0 }, "An undisturbed gesture end did not keep teleoperation selected");
        }
        {
            // Pika role interleavings through the observation seam: a stale writer
            // never clobbers a newer role, and a release always survives.
            var plugin = new PipersPlugin();
            var monitorSnapshot = plugin.telemetry.Pika.Observe();
            Check(plugin.telemetry.Pika.TryAdvance(plugin.telemetry.Pika.Observe(), PikaSessionRole.Owned) &&
                !plugin.telemetry.Pika.TryAdvance(monitorSnapshot, PikaSessionRole.None) && plugin.telemetry.Pika.Role == PikaSessionRole.Owned,
                "A monitor release decided from a stale snapshot overwrote the Owned role Enable set");
            plugin.telemetry.Pika.Release();
            var enableSnapshot = plugin.telemetry.Pika.Observe();
            plugin.telemetry.Pika.Release();
            Check(!plugin.telemetry.Pika.TryAdvance(enableSnapshot, PikaSessionRole.Owned) && plugin.telemetry.Pika.Role == PikaSessionRole.None,
                "An Enable that observed None before a same-role revocation still upgraded to Owned");
        }
        using (var fixture = new Fixture())
        {
            fixture.Rpc.DelayConnect = true;
            fixture.Plugin.approved = true;
            Task enable = fixture.Plugin.Enable(CancellationToken.None, true, null, null);
            try
            {
                await fixture.Rpc.ConnectReached.Task.WaitAsync(TimeSpan.FromSeconds(1));
                fixture.Plugin.telemetry.Pika.Release(); // The telemetry thread revokes while connect is in flight.
                fixture.Rpc.ConnectReply.TrySetResult(new ControlReply { Status = 200, Json = "{\"ok\":true}" });
                bool refused = false;
                try { await enable; } catch (InvalidOperationException error) { refused = error.Message.Contains("Pika session was released"); }
                Check(refused && fixture.Plugin.telemetry.Pika.Role == PikaSessionRole.None && !fixture.Rpc.Routes.Contains("/api/pika/enable"),
                    "Enable's Owned write overwrote a revocation that landed during its connect");
            }
            finally { fixture.Rpc.ConnectReply.TrySetResult(new ControlReply { Status = 200, Json = "{\"ok\":true}" }); }
        }
        foreach (bool resetPending in new[] { true, false })
        {
            using var fixture = new Fixture();
            var telemetry = fixture.Plugin.telemetry;
            var receipt = telemetry.Latest!;
            var snapshot = receipt.State.DeepClone().AsObject();
            snapshot["devices"]![0]!["recovery_required"] = true;
            snapshot["devices"]![0]!["reset_pending"] = resetPending;
            fixture.Plugin.telemetry.PublishState(snapshot);
            telemetry.Publish(receipt with { State = snapshot, Sequence = 2 });
            fixture.Plugin.approved = true;
            bool refused = false;
            try { await fixture.Plugin.Enable(CancellationToken.None, true, null, null); }
            catch (InvalidOperationException error) { refused = error.Message.Contains("Recover robot on Controls"); }
            Check(refused && fixture.Rpc.Routes.IsEmpty,
                "Enable sent a control-purpose recovery verification instead of routing to the Controls recovery card (reset_pending=" + resetPending + ")");
        }
        foreach (StatusCode status in new[] { StatusCode.InvalidArgument, StatusCode.Unimplemented })
        {
            using var fixture = new Fixture();
            fixture.Rpc.CoordinatedRpcFailure = status;
            await fixture.Event("robot.piper-1.stop", default).WaitAsync(TimeSpan.FromSeconds(1));
            Check(fixture.Rpc.Routes.SequenceEqual(new[] { "/api/stop/coordinated", "/api/stop" }),
                "A legacy gRPC coordinator rejection prevented the arm STOP fallback: " + status);
        }
    }

    // STOP returns once the hardware request is answered. Its follow-up
    // (teaching save, panel refresh) runs detached, so a held publication gate
    // cannot delay the next STOP, and a failed follow-up is reported.
    public static async Task RunStopFollowUp()
    {
        const string stop = "robot.piper-1.stop";
        using (var fixture = new Fixture())
        {
            await fixture.Plugin.Render(CancellationToken.None);
            var gate = fixture.Plugin.workspace.PublicationGate;
            await gate.WaitAsync();
            try
            {
                bool first = true, second = true;
                try { await fixture.Event(stop, default).WaitAsync(TimeSpan.FromSeconds(1)); } catch (TimeoutException) { first = false; }
                int sent = fixture.Rpc.Routes.Count;
                try { await fixture.Event(stop, default).WaitAsync(TimeSpan.FromSeconds(1)); } catch (TimeoutException) { second = false; }
                Check(first && sent > 0, "A STOP click waited for the held panel publication gate");
                Check(second && fixture.Rpc.Routes.Count > sent && fixture.Rpc.Routes.All(route => route.StartsWith("/api/stop", StringComparison.Ordinal)),
                    "A second STOP was not sent to the hardware while the first STOP's follow-up was still pending");
                Check(!fixture.Plugin.StopFollowUp.IsCompleted, "The STOP follow-up refreshed the panel without the publication gate");
            }
            finally { gate.Release(); }
            bool finished = true;
            try { await fixture.Plugin.StopFollowUp.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { finished = false; }
            Check(finished && fixture.Plugin.uiActionError.Length == 0, "A STOP follow-up did not finish cleanly once the publication gate was free: " + fixture.Plugin.uiActionError);
        }
        bool editorGone = false;
        using (var fixture = new Fixture((_, _) => editorGone ? Task.FromException(new IOException("editor gone")) : Task.CompletedTask))
        {
            await fixture.Plugin.Render(CancellationToken.None);
            fixture.Plugin.approved = true;
            editorGone = true;
            await fixture.Event(stop, default).WaitAsync(TimeSpan.FromSeconds(1));
            await fixture.Plugin.StopFollowUp.WaitAsync(TimeSpan.FromSeconds(5));
            Check(fixture.Rpc.Routes.Count > 0 && !fixture.Plugin.approved, "STOP was not sent, or kept motion permission, when its panel refresh failed");
            Check(fixture.Plugin.uiActionError.Contains("STOP was sent", StringComparison.Ordinal) && fixture.Plugin.uiActionError.Contains("Panel refresh", StringComparison.Ordinal),
                "A failed STOP follow-up was lost instead of reported: " + fixture.Plugin.uiActionError);
        }
    }

    // B11: a STOP the hardware never answers uses its whole acknowledgement
    // budget. The cleanup after it has its own budget, so it still ends the
    // Pika teaching recording and waits for the observations to be written.
    public static async Task RunStopCleanupBudget()
    {
        using var fixture = new Fixture();
        var plugin = fixture.Plugin;
        await plugin.Render(CancellationToken.None);
        var teaching = plugin.recovery.PikaTeaching;
        var identity = PikaTeachingIdentity.Create("piper-1", "can-one", 1, "/dev/pika-one",
            RobotProfileRouting.Active(plugin.preferences).PikaDistribution, "fake-backend-session");
        teaching.StartRecording(new PikaTeachingRecorder(identity, "free-1", null, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow), "recording");
        fixture.Rpc.StopUnanswered = true;
        try
        {
            await fixture.Event("robot.piper-1.stop", default);
            Check(fixture.Rpc.StopCallsCancelled > 0 && plugin.message.StartsWith("STOP acknowledgement unavailable", StringComparison.Ordinal),
                "The fixture's STOP was answered, so its acknowledgement budget was not used up: " + plugin.message);
            await Eventually(() => plugin.StopFollowUp.IsCompleted, "The follow-up of an unanswered STOP never ended");
            var after = teaching.Current;
            Check(plugin.uiActionError.Length == 0, "Cleanup after a STOP that used its whole budget did not get a budget of its own: " + plugin.uiActionError);
            Check(after is { Recorder: null, Saving: false, Unsaved: null } && File.Exists(after.Path),
                "The STOP follow-up ended before the teaching observations were written: " + after.Status);
        }
        finally { if (Directory.Exists(plugin.engine.Start.SettingsDirectory)) Directory.Delete(plugin.engine.Start.SettingsDirectory, true); }
    }

    // Enable waits for the request its own command acknowledged, even when
    // telemetry ends the shared lease before Enable looks at it.
    public static async Task RunEnableAcknowledgement()
    {
        using var fixture = new Fixture();
        var plugin = fixture.Plugin;
        var arm = plugin.telemetry.State!["devices"]![0]!;
        foreach (var joint in arm["joints"]!.AsArray()) joint!["enabled"] = false;
        plugin.approved = true;
        fixture.Rpc.DelayCommand = true;
        using var telemetryFirst = new LeaseEndingContext(fixture.Authority);
        var caller = SynchronizationContext.Current;
        Task enable;
        SynchronizationContext.SetSynchronizationContext(telemetryFirst);
        try { enable = plugin.Enable(CancellationToken.None, includePika: false); }
        finally { SynchronizationContext.SetSynchronizationContext(caller); }
        await fixture.Rpc.CommandReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // The controller finishes the enable, and telemetry ends the lease, before
        // Enable resumes from its command.
        foreach (var joint in arm["joints"]!.AsArray()) joint!["enabled"] = true;
        plugin.telemetry.State["motion"] = new JsonObject { ["active"] = false, ["request_id"] = 42L };
        plugin.telemetry.Publish(new(plugin.telemetry.State, "fake-backend-session", 2, Stopwatch.GetTimestamp(), 0));
        fixture.Rpc.CommandReply.TrySetResult(new ControlReply { Status = 200, Json = "{\"ok\":true,\"request_id\":42}" });
        string failure = "";
        try { await enable.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
        await Task.Yield(); // leave the context's own thread before disposing it
        Check(telemetryFirst.Ended > 0, "The fixture did not end the enable lease before Enable resumed");
        Check(failure.Length == 0, "Enable lost its own request id when telemetry ended the lease first (" + failure + ")");
    }

    /// <summary>Runs each continuation of the awaiting operation on one thread and
    /// ends any command lease after it, as a telemetry frame arriving between an
    /// acknowledgement and the code that awaited it would.</summary>
    private sealed class LeaseEndingContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> work = [];
        private readonly Thread pump;
        private int ended;
        public int Ended => Volatile.Read(ref ended);

        public LeaseEndingContext(RobotCommandAuthority authority)
        {
            pump = new Thread(() =>
            {
                foreach (var (callback, state) in work.GetConsumingEnumerable())
                {
                    callback(state);
                    if (authority.Current.Lease is { } lease) { authority.EndLease(lease); Interlocked.Increment(ref ended); }
                }
            }) { IsBackground = true };
            pump.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => work.Add((d, state));
        public void Dispose() { work.CompleteAdding(); if (Thread.CurrentThread != pump) pump.Join(); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly PipersPlugin plugin = new();
        public PipersPlugin Plugin => plugin;
        public FakeCalls Rpc { get; } = new();
        public RobotCommandAuthority Authority => plugin.CommandAuthority;
        public SemaphoreSlim Commands => plugin.commands;
        public Task Event(string wire, CancellationToken cancellation) => plugin.HandleEventAsync(new("fixture", wire, "click", JsonSerializer.SerializeToElement(true)), cancellation);

        public Fixture(Func<object, CancellationToken, Task>? send = null)
        {
            plugin.client = new PiperControl.PiperControlClient(Rpc);
            plugin.preferences = RobotProfileRouting.Normalize(new Preferences(Robots:
                [new("piper-1", "Fixture", "can-one", PikaDevice: "/dev/pika-one")], ActiveRobotId: "piper-1"));
            plugin.telemetry.AdoptBackend("fake-backend-session");
            var snapshot = Fixtures.Snapshot("fake-backend-session", Fixtures.Arm("can-one", generation: 1),
                Fixtures.Pika("/dev/pika-one", RobotProfileRouting.Active(plugin.preferences).PikaDistribution, plugin.owner, enabled: true, position: .1));
            plugin.telemetry.PublishState(snapshot);
            plugin.telemetry.Publish(new(snapshot, "fake-backend-session", 1, Stopwatch.GetTimestamp(), 0));
            var context = Fixtures.Context(new PluginStart("fixture", Path.Combine(Path.GetTempPath(), "piper-emergency-" + Guid.NewGuid().ToString("N")), Path.GetTempPath()),
                    send ?? ((_, _) => Task.CompletedTask),
                    (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { applied = false }))));
            plugin.engine = context;
        }

        public void Dispose()
        {
            // StartAsync is never called: no workers or cameras exist. Dispose
            // only the inert channel/gate, without invoking shutdown commands.
            plugin.channel.Dispose();
            Commands.Dispose();
        }
    }

    internal sealed class FakeCalls : CallInvoker
    {
        public ConcurrentQueue<string> Routes { get; } = new();
        public ConcurrentQueue<string> RequestBodies { get; } = new();
        public bool DelayConnect, DelayCommand, CoordinatedMissing;
        /// <summary>A STOP call gets no reply: it ends only when its caller cancels it, as a real call does.</summary>
        public bool StopUnanswered;
        private int stopCallsCancelled;
        public int StopCallsCancelled => Volatile.Read(ref stopCallsCancelled);
        public StatusCode? CoordinatedRpcFailure;
        public bool RequestTokensWereLive { get; private set; } = true;
        public TaskCompletionSource ConnectReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ControlReply> ConnectReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CommandReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ControlReply> CommandReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var command = request as ControlRequest ?? throw new InvalidOperationException("Unexpected fake RPC");
            Routes.Enqueue(command.Route); RequestBodies.Enqueue(command.Json);
            RequestTokensWereLive &= !options.CancellationToken.IsCancellationRequested;
            Task<ControlReply> reply;
            if (command.Route == "/api/pika/connect" && DelayConnect)
            { ConnectReached.TrySetResult(); reply = ConnectReply.Task; }
            else if (command.Route == "/api/command" && DelayCommand)
            { CommandReached.TrySetResult(); reply = CommandReply.Task; }
            else if (StopUnanswered && command.Route.StartsWith("/api/stop", StringComparison.Ordinal))
            {
                var unanswered = new TaskCompletionSource<ControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                options.CancellationToken.Register(() =>
                {
                    Interlocked.Increment(ref stopCallsCancelled);
                    unanswered.TrySetException(new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client.")));
                });
                reply = unanswered.Task;
            }
            else if (command.Route == "/api/stop/coordinated" && CoordinatedRpcFailure is StatusCode failure)
                reply = Task.FromException<ControlReply>(new RpcException(new Status(failure, "Synthetic legacy unsupported route")));
            else reply = Task.FromResult(command.Route == "/api/stop/coordinated"
                ? new ControlReply { Status = CoordinatedMissing ? 404 : 409, Json = "{\"arm\":{\"ok\":true},\"pika\":{\"ok\":false}}" }
                : new ControlReply { Status = 200, Json = "{\"ok\":true}" });
            var response = reply.ContinueWith(result => (TResponse)(object)result.GetAwaiter().GetResult(), TaskScheduler.Default);
            return new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}

using System.Diagnostics;
using System.Text.Json.Nodes;
using Grpc.Core;

namespace LuckyRobots.Piper.Plugin;

/// <summary>One immutable receipt: source age cannot be reset by a slow renderer.</summary>
public sealed record TelemetryReceipt(JsonObject State, string BackendToken, long Sequence,
    long ReceivedTicks, double SourceAgeMilliseconds, bool Connected = true)
{
    /// <summary>Controller evidence older than this never authorizes control.</summary>
    public const double MaximumAgeMilliseconds = 500;
    public double AgeMilliseconds(long nowTicks) => SourceAgeMilliseconds +
        Math.Max(0, Stopwatch.GetElapsedTime(ReceivedTicks, nowTicks).TotalMilliseconds);
    public bool IsFresh(long nowTicks) => Connected && nowTicks >= ReceivedTicks && AgeMilliseconds(nowTicks) <= MaximumAgeMilliseconds;
    /// <summary>Source capture time on the receipt clock. Pass the frequency of the
    /// clock that stamped <see cref="ReceivedTicks"/> and the compared value.</summary>
    public double CapturedTicks(long timestampFrequency) => ReceivedTicks - SourceAgeMilliseconds * timestampFrequency / 1000;
    public double DeviceEvidenceAgeMilliseconds(JsonObject device, long nowTicks)
    {
        if (device["joints"] is not JsonArray { Count: 6 } joints) return double.PositiveInfinity;
        return AgeMilliseconds(nowTicks) + Math.Max(SampleAgeMilliseconds(device["status"]), joints.Max(SampleAgeMilliseconds));
    }
    public double PikaEvidenceAgeMilliseconds(long nowTicks) => PikaEvidenceAgeMilliseconds(State["pika"] as JsonObject, nowTicks);
    public double PikaEvidenceAgeMilliseconds(JsonObject? pika, long nowTicks) => AgeMilliseconds(nowTicks) + SampleAgeMilliseconds(pika);
    public bool HasFreshPika(JsonObject? pika, long nowTicks, double maximumAgeMilliseconds) => IsFresh(nowTicks) &&
        double.IsFinite(maximumAgeMilliseconds) && maximumAgeMilliseconds > 0 &&
        pika?["connected"]?.GetValue<bool>() == true && pika["fresh"]?.GetValue<bool>() == true &&
        PikaEvidenceAgeMilliseconds(pika, nowTicks) <= maximumAgeMilliseconds;
    internal static double SampleAgeMilliseconds(JsonNode? sample) => sample?["age_s"] is JsonValue value &&
        value.TryGetValue<double>(out double seconds) && double.IsFinite(seconds) && seconds >= 0
        ? seconds * 1000 : double.PositiveInfinity;
    public bool HasFreshDevice(JsonObject device, long nowTicks) => IsFresh(nowTicks) && device["fresh"]?.GetValue<bool>() == true &&
        device["joints"] is JsonArray { Count: 6 } joints && joints.All(j => j?["fresh"]?.GetValue<bool>() == true) &&
        DeviceEvidenceAgeMilliseconds(device, nowTicks) <= MaximumAgeMilliseconds;
}

/// <summary>Single-reader publication, with no queue of old robot states.</summary>
public sealed class LatestTelemetry
{
    private TelemetryReceipt? latest;
    private long streamSequence;
    private string streamBackend = "";
    public TelemetryReceipt? Latest => Volatile.Read(ref latest);
    public void BeginStream() { streamSequence = 0; streamBackend = ""; }

    public bool TryPrepare(JsonObject state, long sequence, long sourceUnixMicroseconds, string clockDomain,
        long receivedUnixMicroseconds, long receivedTicks, out TelemetryReceipt? receipt, out string reason)
    {
        receipt = null;
        string backend = state["token"]?.GetValue<string>() ?? "";
        double age = (receivedUnixMicroseconds - (double)sourceUnixMicroseconds) / 1000;
        if (backend.Length == 0 || sequence <= 0 || sourceUnixMicroseconds <= 0 || clockDomain != "host-utc")
        { reason = "Controller feedback has no verified source clock/sequence; update the backend."; return false; }
        if (!double.IsFinite(age) || age < -100 || age > TelemetryReceipt.MaximumAgeMilliseconds)
        { reason = $"Controller feedback arrived outside its {TelemetryReceipt.MaximumAgeMilliseconds:F0} ms freshness window."; return false; }
        if (streamBackend == backend && sequence <= streamSequence)
        { reason = "Discarded duplicate or out-of-order controller feedback."; return false; }
        receipt = new(state, backend, sequence, receivedTicks, Math.Max(0, age));
        reason = ""; return true;
    }

    // The caller invalidates old command scope before publishing a new scope.
    public void Publish(TelemetryReceipt receipt)
    {
        streamBackend = receipt.BackendToken; streamSequence = receipt.Sequence;
        Volatile.Write(ref latest, receipt);
    }
    public void Disconnect()
    {
        var previous = Latest;
        if (previous is not null) Volatile.Write(ref latest, previous with { Connected = false });
    }
}

public sealed record TelemetryScope(string BackendToken, string DeviceId, long Generation)
{
    public static TelemetryScope? Read(JsonObject snapshot, string assignedDevice)
    {
        if (assignedDevice.Length == 0 || snapshot["selected"]?.GetValue<string>() != assignedDevice) return null;
        var device = snapshot["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == assignedDevice);
        return device is null ? null : new(snapshot["token"]!.GetValue<string>(), assignedDevice, device["generation"]!.GetValue<long>());
    }
}

public sealed partial class PipersPlugin
{
    internal readonly TelemetryComponent telemetry = new();
    internal string sceneTrackingStatus = "";
    private string telemetryConnectionUiIssue = "";
    internal string linkBackendToken = "";
    private long scenePermissionEpoch;

    private bool TelemetryIsFresh() => telemetry.IsFresh(Stopwatch.GetTimestamp());
    internal void DisarmTelemetryTargets(string reason, bool clearLink, string? forgetApproval = null, bool forgetAllApprovals = false)
    {
        CommandAuthority.Revoke(forgetApproval, () =>
        {
            approved = linkCommandsEnabled = false;
            recovery.Teaching.Release();
        }, forgetAllApprovals);
        CurrentRecovery?.Cancel();
        follow.CancelInput(reason);
        follow.Viewport.Stop();
        acknowledgedScenePose = null; linkErrorDegrees = null;
        if (clearLink) link = null;
    }

    private void PauseSceneFollowing()
    {
        // Losing a rendered acknowledgement is not losing CAN telemetry. Only
        // viewport-follow permission/gestures are discarded; ordinary controller
        // heartbeats and fresh measured joint controls remain independent.
        Interlocked.Increment(ref scenePermissionEpoch);
        long request = followRequest;
        follow.Viewport.Stop();
        if (CommandAuthority.Current.Lease is { } lease && lease.Request == request) CommandAuthority.EndLease(lease);
        linkCommandsEnabled = false;
        acknowledgedScenePose = null; linkErrorDegrees = null;
    }

    internal void ApplyIncomingScope(TelemetryReceipt receipt)
    {
        var next = receipt.State;
        var scope = TelemetryScope.Read(next, ActiveProfile.CanDeviceId);
        bool backendChanged = token != receipt.BackendToken;
        if (GroupFollowActive)
        {
            if (backendChanged)
            {
                telemetry.Disconnect();
                DisarmTelemetryTargets("Controller backend changed · all previous targets discarded", clearLink: true);
                telemetry.Pika.Release();
            }
            telemetry.AdoptBackend(receipt.BackendToken); telemetry.AdoptScope(scope);
            return; // The group validates every captured lane, independently of the visible profile/selected projection.
        }
        if (backendChanged || telemetry.Scope is not null && scope != telemetry.Scope)
        {
            // Keep the old receipt from granting control under the new epoch
            // between invalidation and publication of the replacement frame.
            telemetry.Disconnect();
            DisarmTelemetryTargets("Controller session or assigned CAN connection changed", clearLink: true); // Also releases the teaching session.
            if (backendChanged) telemetry.Pika.Release();
        }
        telemetry.AdoptBackend(receipt.BackendToken);
        telemetry.AdoptScope(scope);
        if (link is not { } linked) return;
        var device = next["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == linked.DeviceId);
        if (scope != new TelemetryScope(linkBackendToken, linked.DeviceId, linked.Generation) ||
            device?["present"]?.GetValue<bool>() != true || device["up"]?.GetValue<bool>() != true)
        {
            telemetry.Disconnect();
            DisarmTelemetryTargets("Linked robot connection changed", clearLink: true); return;
        }
        var mode = TeachingState.Inspect(device);
        if (mode.BlocksTargets)
        {
            // Observe the transition on the reader even if the editor is busy,
            // so a short teaching interval cannot leave an old drag armed.
            var linkedScope = new TeachingScope(linked.DeviceId, linked.Generation);
            if (recovery.Teaching.Current is not { Owned: true } owned || owned.Scope != linkedScope)
            {
                telemetry.Disconnect();
                CommandAuthority.Revoke(revokeLocalApproval: () =>
                {
                    recovery.Teaching.ObserveHandOver(linkedScope, approved);
                    approved = linkCommandsEnabled = false;
                });
            }
            approved = linkCommandsEnabled = false;
            follow.Viewport.Stop();
            CommandAuthority.ClearTracked();
        }
    }

    internal async Task ReadTelemetry(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var stream = client.WatchState(new(), cancellationToken: cancellation);
                telemetry.BeginStream();
                await foreach (var frame in stream.ResponseStream.ReadAllAsync(cancellation))
                {
                    long receivedTicks = Stopwatch.GetTimestamp();
                    long receivedUtc = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
                    var next = JsonNode.Parse(frame.Json)!.AsObject(); Materialize(next);
                    ProjectProfilePika(next);
                    if (!telemetry.TryPrepare(next, frame.Sequence, frame.TimestampUnixMicroseconds, frame.ClockDomain,
                        receivedUtc, receivedTicks, out var receipt, out string reason))
                    { telemetry.ReportIssue(reason); continue; }
                    ApplyIncomingScope(receipt!);
                    telemetry.PublishState(next);
                    telemetry.Publish(receipt!);
                    ObservePikaTeachingReceipt(receipt!, receivedUtc);
                    telemetry.ReportIssue("");
                }
                if (!cancellation.IsCancellationRequested) throw new IOException("Controller state stream ended.");
            }
            catch (Exception error) when (!cancellation.IsCancellationRequested)
            {
                telemetry.Disconnect();
                PikaTeachingTelemetryInterrupted();
                DisarmTelemetryTargets("Controller telemetry interrupted", clearLink: false); telemetry.Pika.Release();
                string issue = error is RpcException { StatusCode: StatusCode.Unavailable }
                    ? $"Controller unavailable on {ControllerEndpoint.Loopback}:{ControllerEndpoint.GrpcPort}; reconnecting automatically."
                    : "Controller telemetry interrupted: " + error.Message;
                telemetry.ReportIssue(issue);
                message = issue;
                await Task.Delay(1000, cancellation);
            }
        }
    }

    private bool HasFreshControlTelemetry(out TelemetryReceipt? receipt)
    {
        receipt = telemetry.Latest;
        if (receipt is null || !receipt.IsFresh(Stopwatch.GetTimestamp()) || receipt.BackendToken != token) return false;
        JsonObject device;
        try { device = RobotProfileRouting.Device(preferences, receipt.State); }
        catch (InvalidOperationException) { return false; }
        return receipt.HasFreshDevice(device, Stopwatch.GetTimestamp());
    }

    internal void ObserveTelemetryConnectionPresentation(TelemetryReceipt receipt)
    {
        long now = Stopwatch.GetTimestamp();
        if (receipt.BackendToken != token || !receipt.IsFresh(now) || ActiveProfile.CanDeviceId.Length == 0) return;
        string assigned = ActiveProfile.CanDeviceId;
        var device = receipt.State["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == assigned);
        bool selected = receipt.State["selected"]?.GetValue<string>() == assigned;
        string connectionError = device?["connection_error"]?.GetValue<string>() ?? "";
        bool connected = selected && device?["present"]?.GetValue<bool>() == true && device["up"]?.GetValue<bool>() == true &&
            connectionError.Length == 0 && receipt.HasFreshDevice(device, now);
        string previousIssue = telemetryConnectionUiIssue;
        if (connected)
        {
            if (uiActionError == previousIssue) uiActionError = "";
            telemetryConnectionUiIssue = "";
            if (ConnectionMessage(message) || previousIssue.Length > 0 && message == previousIssue)
                message = "Connected · monitoring live robot feedback · previous targets were discarded";
            return;
        }
        bool usbTimeout = connectionError.Contains("WinUSB 121", StringComparison.OrdinalIgnoreCase) || connectionError.Contains("WinUSB121", StringComparison.OrdinalIgnoreCase);
        string adapter = assigned.StartsWith("usb:", StringComparison.Ordinal) && assigned.Length > 7 ? " USB-CAN …" + assigned[^4..] : " USB-CAN adapter";
        string detail = usbTimeout ? "The" + adapter + " is not responding. Reconnect only this adapter’s USB cable; leave robot power and CAN connected. Details: " + connectionError
            : !selected ? "The assigned CAN adapter is not active. Select this robot to activate its connection."
            : device is null ? "The assigned USB-CAN adapter is missing."
            : connectionError.Length > 0 ? connectionError
            : device["blocker"]?.GetValue<string>() is { Length: > 0 } blocker ? blocker
            : "Waiting for fresh status and joint feedback from the assigned CAN adapter.";
        string issue = ActiveProfile.Name + " · CAN feedback unavailable: " + detail;
        if (uiActionError.Length == 0 || uiActionError == previousIssue || ConnectionMessage(uiActionError)) uiActionError = issue;
        if (ConnectionMessage(message) || previousIssue.Length > 0 && message == previousIssue) message = issue;
        telemetryConnectionUiIssue = issue;

        static bool ConnectionMessage(string value) => value.StartsWith("Connecting to", StringComparison.Ordinal) ||
            value.StartsWith("Controller unavailable", StringComparison.Ordinal) || value.StartsWith("Controller feedback delayed", StringComparison.Ordinal) ||
            value.StartsWith("Controller telemetry interrupted", StringComparison.Ordinal);
    }

    // Stale-feedback watchdog state; touched only by the arm heartbeat loop.
    private bool staleFeedbackDisarmed;

    // Everything a stale-feedback disarm withdraws. While none of it stands, a
    // repeated disarm would change nothing except the permission epoch.
    private bool MotionGrantStanding() => approved || linkCommandsEnabled || teleoperating || recovery.Teaching.Current.Owned ||
        CommandAuthority.Current is { Lease: not null } or { Confirmation: not null } ||
        CurrentRecovery is { } running && !running.Token.IsCancellationRequested;

    internal async Task ArmHeartbeats(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        do await ArmHeartbeatTick(cancellation);
        while (await timer.WaitForNextTickAsync(cancellation));
    }

    /// <summary>One 200 ms watchdog pass: disarm on stale feedback, otherwise renew the tracked lease.</summary>
    internal async Task ArmHeartbeatTick(CancellationToken cancellation)
    {
        var group = follow.Session;
        var receipt = telemetry.Latest;
        if (group is null && !HasFreshControlTelemetry(out receipt))
        {
            if (RecoveryExpectedGap(receipt)) return;
            // Revoke once on the fresh-to-stale edge. Revoking on every tick would
            // advance the permission epoch five times a second, so an operator
            // question raised during the gap (reset completion) would go inert at
            // once. A grant that appears while feedback is still stale is revoked
            // on the next tick.
            if (staleFeedbackDisarmed && !MotionGrantStanding()) return;
            staleFeedbackDisarmed = true;
            DisarmTelemetryTargets($"Measured robot feedback exceeded the {TelemetryReceipt.MaximumAgeMilliseconds:F0} ms freshness window", clearLink: false);
            return;
        }
        if (group is null) staleFeedbackDisarmed = false;
        if (CommandAuthority.Current.Lease is not { } lease) return;
        var scope = lease.Scope;
        JsonNode? motion;
        if (group is null)
        {
            if (scope.ProfileId != ActiveProfile.Id || scope.Backend != receipt!.BackendToken ||
                TelemetryScope.Read(receipt.State, scope.Device) != new TelemetryScope(scope.Backend, scope.Device, scope.Generation))
            { CommandAuthority.EndLease(lease); return; }
            motion = receipt.State["motion"];
        }
        else
        {
            // Group requests have their own heartbeat. Preserve a previously
            // admitted independent arm's lease without consulting UI selection.
            if (group.Lanes.Any(lane => lane.Robot.DeviceId == scope.Device) ||
                receipt is null || receipt.BackendToken != scope.Backend || scope.Backend != token)
                return;
            var devices = (receipt.State["devices"] as JsonArray)?.OfType<JsonObject>()
                .Where(d => d["id"]?.GetValue<string>() == scope.Device).Take(2).ToArray();
            if (devices is not { Length: 1 } || devices[0]["generation"]?.GetValue<long>() != scope.Generation ||
                !receipt.HasFreshDevice(devices[0], Stopwatch.GetTimestamp())) return;
            if (devices[0]["motion"] is not JsonObject laneMotion || laneMotion["device_id"]?.GetValue<string>() != scope.Device ||
                laneMotion["generation"]?.GetValue<long>() != scope.Generation) return;
            motion = laneMotion;
        }
        long request = lease.Request;
        long reportedRequest = motion?["request_id"]?.GetValue<long>() ?? 0;
        if (reportedRequest < request) return; // Admission feedback has not arrived yet.
        if (reportedRequest != request || motion?["active"]?.GetValue<bool>() != true || motion["owner"]?.GetValue<string>() != owner)
        { CommandAuthority.EndLease(lease); return; }
        if (!ReferenceEquals(CommandAuthority.Current.Lease, lease)) return;
        try { await Call("/api/heartbeat", new JsonObject { ["request_id"] = request, ["owner"] = owner,
            ["device_id"] = scope.Device, ["generation"] = scope.Generation }, cancellation, scope.Backend); }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        { CommandAuthority.EndLease(lease); message = "Arm heartbeat unavailable: " + error.Message; }
    }

    internal async Task PikaHeartbeats(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        var pending = new Dictionary<(string Backend, string Device, string? Distribution), Task>();
        try
        {
            do
            {
                foreach (var completed in pending.Where(entry => entry.Value.IsCompleted).Select(entry => entry.Key).ToArray())
                    pending.Remove(completed);
                var receipt = telemetry.Latest;
                if (receipt is null || !receipt.IsFresh(Stopwatch.GetTimestamp()) || receipt.BackendToken != token) continue;
                var profile = ActiveProfile;
                foreach (var session in OwnedPikaSessions(receipt.State))
                {
                    var key = (receipt.BackendToken, session["device"]!.GetValue<string>(), session["distribution"]?.GetValue<string>());
                    if (!pending.ContainsKey(key)) pending.Add(key, Renew(key, ProfilePikaRouting.Matches(profile, session) ? profile : null));
                }
            } while (await timer.WaitForNextTickAsync(cancellation));
        }
        finally { await Task.WhenAll(pending.Values); }

        async Task Renew((string Backend, string Device, string? Distribution) key, RobotProfile? selected)
        {
            try
            {
                await Call("/api/pika/heartbeat", new JsonObject { ["owner"] = owner, ["pika_device"] = key.Device,
                    ["pika_distribution"] = key.Distribution }, cancellation, key.Backend);
            }
            catch (Exception error)
            {
                if (cancellation.IsCancellationRequested) return;
                if (selected is not null && token == key.Backend && ActiveProfile is { } current && current.Id == selected.Id &&
                    current.PikaDevice == selected.PikaDevice && current.WslDistribution == selected.WslDistribution)
                {
                    telemetry.Pika.Release();
                    message = "Pika connection: " + error.Message;
                }
                else Trace.TraceWarning("Independent Pika heartbeat unavailable: {0}", error.Message);
            }
        }
    }

    private async Task ProcessTelemetry(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await MaintainCameraDiscovery(cancellation);
                var receipt = telemetry.Latest;
                if (receipt is null || !receipt.IsFresh(Stopwatch.GetTimestamp()) || receipt.BackendToken != token)
                {
                    sceneTrackingStatus = "Measured scene paused · waiting for fresh controller feedback";
                    ObserveReplayScope(null);
                    message = telemetry.Issue is { Length: > 0 } issue ? issue : "Controller feedback delayed · motion permission cleared; waiting for fresh telemetry";
                    QueueHeightMaintenance();
                    await Update(cancellation);
                    await Task.Delay(100, cancellation); continue;
                }
                var next = receipt.State;
                await ObserveProfileRouting(next, cancellation);
                RestoreControlApproval();
                await ObserveTeaching(cancellation);
                await ObserveHandGuideRecovery(cancellation);
                await RestoreMeasuredTracking(cancellation);
                ObserveTelemetryConnectionPresentation(receipt);
                ObserveReplayScope(receipt);
                RetryLinkedPikaMonitor();
                if (CommandAuthority.Current.LastCommand is { } last && last.Scope.Backend == receipt.BackendToken &&
                    last.Scope.ProfileId == ActiveProfile.Id && next["selected"]?.GetValue<string>() == last.Scope.Device &&
                    next["motion"]?["request_id"]?.GetValue<long>() == last.Request)
                {
                    message = next["message"]?.GetValue<string>() ?? message;
                    if (next["motion"]?["active"]?.GetValue<bool>() != true) CommandAuthority.EndStatus(last);
                }
                await PublishMeasuredGhosts(cancellation);
                await PublishMeasuredScene(cancellation);
                QueueWizardHighlight();
                await Update(cancellation);
            }
            catch (Exception error) when (!cancellation.IsCancellationRequested)
            {
                // A scene/stdio timeout is not a lost CAN stream. The reader and
                // controller heartbeats keep running, and no binding is replaced.
                PauseSceneFollowing();
                sceneTrackingStatus = "Scene view delayed/unavailable: " + error.Message;
                Trace.TraceWarning("Measured scene/UI update: {0}", error.Message);
            }
            QueueHeightMaintenance();
            await Task.Delay(100, cancellation);
        }
    }

    internal async Task PublishMeasuredScene(CancellationToken cancellation)
    {
        var group = follow.Session;
        if (group is not null && !GroupWaitingForUserPlay(group)) return;
        if (link is not { } linked) return;
        bool SamePublishingScope() => ReferenceEquals(link, linked) &&
            ReferenceEquals(follow.Session, group) &&
            (group is null || GroupWaitingForUserPlay(group));
        // Arming Follow in Edit does not transfer pose ownership. Keep the
        // measured pose lease alive until the actual Play runtime takes over;
        // otherwise the editor motion graph restores its authored (open) jaws.
        if (!SamePublishingScope()) return;
        var receipt = telemetry.Latest;
        if (receipt is null || !receipt.IsFresh(Stopwatch.GetTimestamp()) || receipt.BackendToken != linkBackendToken)
        { linkErrorDegrees = null; sceneTrackingStatus = "Measured scene paused · controller feedback is stale"; return; }
        if (RecoveryExpectedGap(receipt))
        { linkErrorDegrees = null; sceneTrackingStatus = "Measured scene paused during supported controller reset"; return; }
        JsonObject device;
        try { device = RobotProfileRouting.Device(preferences, receipt.State); }
        catch (InvalidOperationException) { linkErrorDegrees = null; return; }
        if (!receipt.HasFreshDevice(device, Stopwatch.GetTimestamp()))
        { linkErrorDegrees = null; sceneTrackingStatus = "Measured scene paused · waiting for fresh joints"; return; }
        try
        {
            if (device["recovery_required"]?.GetValue<bool>() == true) linkCommandsEnabled = false;
            var requested = linked.Feedback(device, receipt.State["pika"] as JsonObject, PikaMatchesProfile(),
                PikaDisplayReferenceAngle(receipt.State["pika"] as JsonObject));
            var motion = receipt.State["motion"];
            if (teleoperating && motion?["finishing_target"]?.GetValue<bool>() == true && motion["owner"]?.GetValue<string>() == owner)
            {
                int index = Array.IndexOf(linked.JointNames, followJoint);
                double? target = index >= 0 ? device["joints"]?[index]?["target_deg"]?.GetValue<double>() : null;
                if (target.HasValue) { requested["leaderJoint"] = followJoint; requested["leaderPosition"] = target.Value * Math.PI / 180; }
            }
            using var sceneDeadline = group is null ? CancellationTokenSource.CreateLinkedTokenSource(cancellation)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellation, group.Stop.Token);
            sceneDeadline.CancelAfter(TimeSpan.FromMilliseconds(400));
            if (!SamePublishingScope()) return;
            // The native API is Edit-only and rejects a Play transition before
            // mutating any joints, even when it races this final local check.
            var reply = await engine.RequestAsync("robot.feedback", requested, sceneDeadline.Token);
            var latest = telemetry.Latest;
            if (!SamePublishingScope() || latest is null || !latest.IsFresh(Stopwatch.GetTimestamp()) ||
                TelemetryScope.Read(latest.State, ActiveProfile.CanDeviceId) != new TelemetryScope(receipt.BackendToken, linked.DeviceId, linked.Generation)) return;
            if (teleoperating && (!linkCommandsEnabled || reply.TryGetProperty("teleoperating", out var mode) && !mode.GetBoolean())) PauseSceneFollowing();
            if (reply.TryGetProperty("applied", out var applied) && applied.GetBoolean())
            {
                double age = receipt.DeviceEvidenceAgeMilliseconds(device, Stopwatch.GetTimestamp());
                if (age > TelemetryReceipt.MaximumAgeMilliseconds)
                {
                    PauseSceneFollowing(); sceneTrackingStatus = $"Scene view catching up · measured pose {age:F0} ms old"; return;
                }
                var acknowledgement = JsonNode.Parse(reply.GetRawText())!.AsObject();
                linkErrorDegrees = linked.FeedbackError(requested, acknowledgement);
                acknowledgedScenePose = acknowledgement;
                ObserveHeightFeedback(receipt, device, acknowledgement);
                sceneTrackingStatus = $"Measured scene live · pose age {age:F0} ms";
            }
            else if (teleoperating && reply.TryGetProperty("teleoperating", out var following) && following.GetBoolean())
            {
                // Native teleoperation intentionally retains the leader target
                // and draws measured feedback as a ghost instead of overwriting
                // the edited pose. This is not an editor rejection or a reason
                // to terminate a valid drag/finishing target.
                linkErrorDegrees = null; acknowledgedScenePose = null;
                sceneTrackingStatus = "Leader target + measured ghost · calibration capture paused";
            }
            else
            {
                PauseSceneFollowing();
                sceneTrackingStatus = "Measured scene update deferred · finish the active viewport edit";
            }
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            // A rejected old publication cannot revoke a replacement link or
            // the runtime leader that took ownership while it was pending.
            if (!ReferenceEquals(link, linked) || !ReferenceEquals(follow.Session, group)) return;
            if (group is not null && GroupScopeCurrent(group) && !GroupWaitingForUserPlay(group) &&
                error.Message.Contains("Robot linking requires an open scene in Edit mode", StringComparison.Ordinal))
            {
                // Expected boundary: Follow owns the upcoming runtime, while
                // this final measured Edit update lost the race to Play.
                acknowledgedScenePose = null; linkErrorDegrees = null;
                sceneTrackingStatus = "Simulation is taking control · measured Edit tracking paused";
                return;
            }
            if (group is not null && !GroupWaitingForUserPlay(group))
            {
                // A late Edit failure has no authority over an active runtime
                // leader. Its worker owns runtime failures and final holds.
                Trace.TraceWarning("Measured Edit update retired after group runtime takeover: {0}", error.Message);
                return;
            }
            PauseSceneFollowing();
            measuredTrackingSceneUnverified = true;
            sceneTrackingStatus = "Scene view delayed/unavailable: " + error.Message;
        }
    }
}

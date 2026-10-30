using System.Diagnostics;
using System.Text.Json.Nodes;
using Grpc.Core;

namespace LuckyRobots.Piper.Plugin;

/// <summary>One immutable receipt: source age cannot be reset by a slow renderer.</summary>
public sealed record TelemetryReceipt(JsonObject State, string BackendToken, long Sequence,
    long ReceivedTicks, double SourceAgeMilliseconds, bool Connected = true)
{
    public double AgeMilliseconds(long nowTicks) => SourceAgeMilliseconds +
        Math.Max(0, Stopwatch.GetElapsedTime(ReceivedTicks, nowTicks).TotalMilliseconds);
    public bool IsFresh(long nowTicks) => Connected && nowTicks >= ReceivedTicks && AgeMilliseconds(nowTicks) <= 500;
    public double DeviceEvidenceAgeMilliseconds(JsonObject device, long nowTicks)
    {
        double SampleAge(JsonNode? sample) => sample?["age_s"] is JsonValue value && value.TryGetValue<double>(out double seconds) &&
            double.IsFinite(seconds) && seconds >= 0 ? seconds * 1000 : double.PositiveInfinity;
        if (device["joints"] is not JsonArray { Count: 6 } joints) return double.PositiveInfinity;
        return AgeMilliseconds(nowTicks) + Math.Max(SampleAge(device["status"]), joints.Max(SampleAge));
    }
    public bool HasFreshDevice(JsonObject device, long nowTicks) => IsFresh(nowTicks) && device["fresh"]?.GetValue<bool>() == true &&
        device["joints"] is JsonArray { Count: 6 } joints && joints.All(j => j?["fresh"]?.GetValue<bool>() == true) &&
        DeviceEvidenceAgeMilliseconds(device, nowTicks) <= 500;
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
        if (!double.IsFinite(age) || age < -100 || age > 500)
        { reason = "Controller feedback arrived outside its 500 ms freshness window."; return false; }
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
    private readonly LatestTelemetry telemetry = new();
    private TelemetryScope? telemetryScope;
    private string telemetryIssue = "Waiting for controller feedback", sceneTrackingStatus = "";
    private string? alignmentInterruption;
    private string linkBackendToken = "";
    private bool telemetryTargetsDisarmed;
    private long targetPermissionEpoch;
    private long scenePermissionEpoch;

    private bool TelemetryIsFresh() => telemetry.Latest is { } receipt && receipt.BackendToken == token && receipt.IsFresh(Stopwatch.GetTimestamp());

    private void DisarmTelemetryTargets(string reason, bool clearLink)
    {
        Interlocked.Increment(ref targetPermissionEpoch);
        approved = linkCommandsEnabled = teleoperating = false;
        followRequest = lease = lastCommand = 0; followInput.End();
        acknowledgedScenePose = null; acknowledgedSceneAt = 0; linkErrorDegrees = null;
        if (clearLink) { link = null; recoveryInProgress = false; }
        if (!telemetryTargetsDisarmed) Interlocked.Exchange(ref alignmentInterruption, reason);
        telemetryTargetsDisarmed = true;
    }

    private void PauseSceneFollowing()
    {
        // Losing a rendered acknowledgement is not losing CAN telemetry. Only
        // viewport-follow permission/gestures are discarded; ordinary controller
        // heartbeats and fresh measured joint controls remain independent.
        Interlocked.Increment(ref scenePermissionEpoch);
        teleoperating = false; followInput.End();
        if (followRequest != 0 && lease == followRequest) lease = 0;
        followRequest = 0; linkCommandsEnabled = false;
        acknowledgedScenePose = null; acknowledgedSceneAt = 0; linkErrorDegrees = null;
    }

    private void ApplyIncomingScope(TelemetryReceipt receipt)
    {
        var next = receipt.State;
        var scope = TelemetryScope.Read(next, ActiveProfile.CanDeviceId);
        bool backendChanged = token != receipt.BackendToken;
        if (backendChanged || telemetryScope is not null && scope != telemetryScope)
        {
            DisarmTelemetryTargets("Controller session or assigned CAN connection changed", clearLink: true);
            teachingSeen = approvalBeforeTeaching = false;
            if (backendChanged) pikaOwned = pikaMonitoring = false;
        }
        token = receipt.BackendToken;
        telemetryScope = scope;
        if (link is not { } linked) return;
        var device = next["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == linked.DeviceId);
        if (scope != new TelemetryScope(linkBackendToken, linked.DeviceId, linked.Generation) ||
            device?["present"]?.GetValue<bool>() != true || device["up"]?.GetValue<bool>() != true)
        {
            DisarmTelemetryTargets("Linked robot connection changed", clearLink: true); return;
        }
        var mode = TeachingState.Inspect(device);
        if (mode.BlocksTargets)
        {
            // Observe the transition on the reader even if the editor is busy,
            // so a short teaching interval cannot leave an old drag armed.
            if (!teachingSeen)
            {
                Interlocked.Increment(ref targetPermissionEpoch);
                approvalBeforeTeaching = approved; teachingSeen = true;
                teachingDevice = linked.DeviceId; teachingGeneration = linked.Generation;
            }
            approved = linkCommandsEnabled = teleoperating = false;
            followRequest = lease = lastCommand = 0; followInput.End();
        }
    }

    private async Task ReadTelemetry(CancellationToken cancellation)
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
                    if (!telemetry.TryPrepare(next, frame.Sequence, frame.TimestampUnixMicroseconds, frame.ClockDomain,
                        receivedUtc, receivedTicks, out var receipt, out string reason))
                    { telemetryIssue = reason; continue; }
                    ApplyIncomingScope(receipt!);
                    state = next;
                    telemetry.Publish(receipt!);
                    telemetryIssue = "";
                }
                if (!cancellation.IsCancellationRequested) throw new IOException("Controller state stream ended.");
            }
            catch (Exception error) when (!cancellation.IsCancellationRequested)
            {
                telemetry.Disconnect();
                DisarmTelemetryTargets("Controller telemetry interrupted", clearLink: false);
                pikaOwned = pikaMonitoring = false;
                telemetryIssue = error is RpcException { StatusCode: StatusCode.Unavailable }
                    ? "Controller unavailable on 127.0.0.1:8766; reconnecting automatically."
                    : "Controller telemetry interrupted: " + error.Message;
                message = telemetryIssue;
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

    private async Task ArmHeartbeats(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        do
        {
            if (!HasFreshControlTelemetry(out var receipt))
            {
                bool expectedReset = receipt is not null && receipt.IsFresh(Stopwatch.GetTimestamp()) && link is { } linked &&
                    TeachingResetAssisted.ExpectedFeedbackGap(teachingResetBusy, receipt.State, linked.DeviceId, linked.Generation);
                if (!expectedReset) DisarmTelemetryTargets("Measured robot feedback exceeded the 500 ms freshness window", clearLink: false);
                continue;
            }
            telemetryTargetsDisarmed = false;
            long request = lease;
            if (request == 0) continue;
            var motion = receipt!.State["motion"];
            long reportedRequest = motion?["request_id"]?.GetValue<long>() ?? 0;
            if (reportedRequest < request) continue; // Admission feedback has not arrived yet.
            if (reportedRequest != request || motion?["active"]?.GetValue<bool>() != true || motion["owner"]?.GetValue<string>() != owner)
            { if (lease == request) lease = 0; continue; }
            try { await Call("/api/heartbeat", new JsonObject { ["request_id"] = request, ["owner"] = owner }, cancellation, receipt.BackendToken); }
            catch (Exception error) when (!cancellation.IsCancellationRequested)
            { if (lease == request) lease = 0; message = "Arm heartbeat unavailable: " + error.Message; }
        } while (await timer.WaitForNextTickAsync(cancellation));
    }

    private async Task PikaHeartbeats(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        do
        {
            var receipt = telemetry.Latest;
            if (!pikaOwned && !pikaMonitoring || receipt is null || !receipt.IsFresh(Stopwatch.GetTimestamp()) || receipt.BackendToken != token) continue;
            try { await Call("/api/pika/heartbeat", new JsonObject { ["owner"] = owner }, cancellation, receipt.BackendToken); }
            catch (Exception error) when (!cancellation.IsCancellationRequested)
            { pikaOwned = pikaMonitoring = false; message = "Pika connection: " + error.Message; }
        } while (await timer.WaitForNextTickAsync(cancellation));
    }

    private async Task ProcessTelemetry(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (Interlocked.Exchange(ref alignmentInterruption, null) is { } interrupted)
                    await FinishAlignment(interrupted, cancellation);
                var receipt = telemetry.Latest;
                if (receipt is null || !receipt.IsFresh(Stopwatch.GetTimestamp()) || receipt.BackendToken != token)
                {
                    sceneTrackingStatus = "Measured scene paused · waiting for fresh controller feedback";
                    message = telemetryIssue.Length > 0 ? telemetryIssue : "Controller feedback delayed · motion permission cleared; waiting for fresh telemetry";
                    await Update(cancellation);
                    await Task.Delay(100, cancellation); continue;
                }
                var next = receipt.State;
                await ObserveProfileRouting(next, cancellation);
                await ObserveTeaching(cancellation);
                if (message.StartsWith("Connecting to", StringComparison.Ordinal) || message.StartsWith("Controller unavailable", StringComparison.Ordinal) || message.StartsWith("Controller feedback delayed", StringComparison.Ordinal))
                    message = "Connected · monitoring live robot feedback · previous targets were discarded";
                RetryLinkedPikaMonitor();
                if (lastCommand != 0 && next["motion"]?["request_id"]?.GetValue<long>() == lastCommand)
                {
                    message = next["message"]?.GetValue<string>() ?? message;
                    if (next["motion"]?["active"]?.GetValue<bool>() != true) lastCommand = 0;
                }
                await PublishMeasuredScene(cancellation);
                if (recoveryInProgress && HasFreshControlTelemetry(out var recoveryReceipt))
                {
                    var device = Device();
                    if (device["recovery_required"]?.GetValue<bool>() != true && device["reset_pending"]?.GetValue<bool>() != true)
                    { recoveryInProgress = false; message = "Recovery verified. The interrupted move was discarded. Enable robot when ready; measured scene tracking continues."; }
                    else if (RecoveryPlan.Inspect(device, recoveryReceipt!.State["motion"]?["active"]?.GetValue<bool>() == true).Action == "verify-reset" && await commands.WaitAsync(0, cancellation))
                    {
                        try { await Command("verify-reset", null, cancellation, recoveryAcknowledged: true); recoveryInProgress = false; }
                        catch (InvalidOperationException error) { message = error.Message; recoveryInProgress = false; }
                        finally { commands.Release(); }
                    }
                }
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
            await Task.Delay(100, cancellation);
        }
    }

    private async Task PublishMeasuredScene(CancellationToken cancellation)
    {
        if (link is not { } linked) return;
        var receipt = telemetry.Latest;
        if (receipt is null || !receipt.IsFresh(Stopwatch.GetTimestamp()) || receipt.BackendToken != linkBackendToken)
        { linkErrorDegrees = null; sceneTrackingStatus = "Measured scene paused · controller feedback is stale"; return; }
        if (TeachingResetAssisted.ExpectedFeedbackGap(teachingResetBusy, receipt.State, linked.DeviceId, linked.Generation))
        { linkErrorDegrees = null; sceneTrackingStatus = "Measured scene paused during supported controller reset"; return; }
        JsonObject device;
        try { device = RobotProfileRouting.Device(preferences, receipt.State); }
        catch (InvalidOperationException) { linkErrorDegrees = null; return; }
        if (!receipt.HasFreshDevice(device, Stopwatch.GetTimestamp()))
        { linkErrorDegrees = null; sceneTrackingStatus = "Measured scene paused · waiting for fresh joints"; return; }
        try
        {
            if (device["recovery_required"]?.GetValue<bool>() == true) linkCommandsEnabled = false;
            var requested = linked.Feedback(device, receipt.State["pika"] as JsonObject, PikaMatchesProfile());
            var motion = receipt.State["motion"];
            if (teleoperating && motion?["finishing_target"]?.GetValue<bool>() == true && motion["owner"]?.GetValue<string>() == owner)
            {
                int index = Array.IndexOf(linked.JointNames, followJoint);
                double? target = index >= 0 ? device["joints"]?[index]?["target_deg"]?.GetValue<double>() : null;
                if (target.HasValue) { requested["leaderJoint"] = followJoint; requested["leaderPosition"] = target.Value * Math.PI / 180; }
            }
            using var sceneDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            sceneDeadline.CancelAfter(TimeSpan.FromMilliseconds(400));
            var reply = await engine.RequestAsync("robot.feedback", requested, sceneDeadline.Token);
            var latest = telemetry.Latest;
            if (!ReferenceEquals(link, linked) || latest is null || !latest.IsFresh(Stopwatch.GetTimestamp()) ||
                TelemetryScope.Read(latest.State, ActiveProfile.CanDeviceId) != new TelemetryScope(receipt.BackendToken, linked.DeviceId, linked.Generation)) return;
            if (teleoperating && (!linkCommandsEnabled || reply.TryGetProperty("teleoperating", out var mode) && !mode.GetBoolean())) PauseSceneFollowing();
            if (reply.TryGetProperty("applied", out var applied) && applied.GetBoolean())
            {
                double age = receipt.DeviceEvidenceAgeMilliseconds(device, Stopwatch.GetTimestamp());
                if (age > 500)
                {
                    PauseSceneFollowing(); sceneTrackingStatus = $"Scene view catching up · measured pose {age:F0} ms old"; return;
                }
                var acknowledgement = JsonNode.Parse(reply.GetRawText())!.AsObject();
                linkErrorDegrees = linked.FeedbackError(requested, acknowledgement);
                acknowledgedScenePose = acknowledgement;
                acknowledgedSceneAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)age;
                sceneTrackingStatus = $"Measured scene live · pose age {age:F0} ms";
                SampleAlignment(device);
            }
            else if (teleoperating && reply.TryGetProperty("teleoperating", out var following) && following.GetBoolean())
            {
                // Native teleoperation intentionally retains the leader target
                // and draws measured feedback as a ghost instead of overwriting
                // the edited pose. This is not an editor rejection or a reason
                // to terminate a valid drag/finishing target.
                linkErrorDegrees = null; acknowledgedScenePose = null; acknowledgedSceneAt = 0;
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
            PauseSceneFollowing();
            sceneTrackingStatus = "Scene view delayed/unavailable: " + error.Message;
        }
    }
}

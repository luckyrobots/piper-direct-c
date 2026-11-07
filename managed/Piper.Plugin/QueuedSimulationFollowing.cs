using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public static class OrderedFrameReadRetry
{
    public static bool ExpectedStop(bool attemptDeadlineExpired, bool stopRequested, bool ending) =>
        !attemptDeadlineExpired && stopRequested && ending;
    // A lost read-only response does not retire source nodes. Retry from the
    // same cursor only while the independent physical source lease is alive.
    public static bool Allowed(double validatedEnvelopeAgeMilliseconds, bool cancellationRequested, bool sameScope, bool publisherRunning) =>
        !cancellationRequested && sameScope && publisherRunning && double.IsFinite(validatedEnvelopeAgeMilliseconds) &&
        validatedEnvelopeAgeMilliseconds >= 0 && validatedEnvelopeAgeMilliseconds < SimulationFollowPolicy.MaximumSourceAgeMilliseconds;
}

public sealed partial class PipersPlugin
{
    private async Task WaitForNativeAdmissionReference(SimulationSession session, long acknowledgementSequence, CancellationToken cancellation)
    {
        long began = Stopwatch.GetTimestamp();
        long acknowledged = Volatile.Read(ref session.ArmAdmissionRetiredTicks);
        long minimumNativeSequence = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var receipt = telemetry.Latest ?? throw new InvalidOperationException("Native admission feedback is unavailable.");
            // Validate faults, power and ownership against this same receipt
            // before deciding that an older native request is merely cached.
            _ = SimulationHardware(session, sourceReceipt: receipt);
            session.Operation = "await-native-follow-reference";
            bool ready = SimulationFollowNativeReference.AdmissionReady(receipt, session.Backend, session.Robot.DeviceId,
                session.Robot.Generation, owner, session.ArmRequest, Interlocked.Read(ref session.Sequence),
                acknowledged, acknowledgementSequence, Stopwatch.GetTimestamp(), minimumNativeSequence);
            if (receipt.State["motion"]?["request_id"]?.GetValue<long>() == session.ArmRequest)
                minimumNativeSequence = receipt.State["motion"]!["follow_sequence"]!.GetValue<long>();
            if (ready) return;
            if (Stopwatch.GetElapsedTime(began).TotalMilliseconds >= 400 ||
                acknowledged <= 0 || Stopwatch.GetElapsedTime(acknowledged).TotalSeconds >= 2)
                throw new InvalidOperationException("Native following admission was acknowledged, but a fresh post-acknowledgement reference did not arrive within the existing preparation deadline.");
            simulationStatus = "Starting linked playback · waiting for fresh controller reference · simulation stays paused";
            await Task.Delay(10, cancellation);
        }
    }

    private sealed class OrderedFrameReadCancellation(OperationCanceledException cause, bool deadlineExpired, bool expectedStop)
        : OperationCanceledException(cause.Message, cause, cause.CancellationToken)
    {
        public bool DeadlineExpired { get; } = deadlineExpired;
        public bool ExpectedStop { get; } = expectedStop;
    }

    private bool RetryOrderedFrameRead(SimulationSession session, CancellationToken cancellation)
    {
        long validated = Volatile.Read(ref session.OrderedEnvelopeTicks);
        return session.PlayStarted && session.Ordered is not null && validated != 0 &&
            OrderedFrameReadRetry.Allowed(Stopwatch.GetElapsedTime(validated).TotalMilliseconds, cancellation.IsCancellationRequested,
                SimulationScopeCurrent(session), session.OrderedPublisher is { IsCompleted: false });
    }

    private async Task<JsonObject> ReadOrderedFrames(SimulationSession session, CancellationToken cancellation, JsonObject? start = null)
    {
        session.Operation = "robot.runtime.frames";
        var request = SimulationRequest(session, start is null ? null : "start");
        if (start is not null) foreach (var property in start) request[property.Key] = property.Value?.DeepClone();
        lock (session.OrderedGate)
        {
            request["afterFrameCursor"] = (session.Ordered?.Path.LastCursor ?? 0).ToString(CultureInfo.InvariantCulture);
            int space = OrderedSimulationPath.MaximumFrames - (session.Ordered?.Path.FrameCount ?? 0);
            if (space <= 0) throw new InvalidOperationException("The ordered physical follower buffer is full; no path frames were discarded.");
            request["maxFrames"] = Math.Min(128, space);
        }
        using var attemptDeadline = new CancellationTokenSource(start is null ? 150 : 400);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, session.Stop.Token, attemptDeadline.Token);
        try { return JsonNode.Parse((await engine.RequestAsync("robot.runtime.frames", request, timeout.Token)).GetRawText())!.AsObject(); }
        catch (OperationCanceledException error) when (start is null)
        {
            bool expired = attemptDeadline.IsCancellationRequested;
            // The per-read link may cancel before the outer worker's link is
            // notified. Preserve a real expired deadline even if Stop follows.
            throw new OrderedFrameReadCancellation(error, expired,
                OrderedFrameReadRetry.ExpectedStop(expired, session.Stop.IsCancellationRequested, Volatile.Read(ref session.Ending) != 0));
        }
    }

    private async Task PrepareOrderedSource(SimulationSession session, JsonObject raw, SimulationFollowSample sample,
        SimulationFollowHardware hardware, SimulationFollowPolicy policy, CancellationToken cancellation)
    {
        await ControlSimulation(session, "claim-episode-follower", cancellation, properties: new JsonObject { ["episodeId"] = raw["episodeId"]?.GetValue<string>() ?? "" });
        var start = session.Robot.OrderedCaptureMapping(raw);
        session.Ordered = new(sample, hardware, preferences.Speed / 100d);
        long began = Stopwatch.GetTimestamp();
        var response = await ReadOrderedFrames(session, cancellation, start);
        var pinned = SimulationFollowSample.Read(response, session.Robot.JointPrefix);
        policy.ValidateReady(pinned, SimulationHardware(session), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Stopwatch.GetElapsedTime(began).TotalMilliseconds);
        if (response["prepareId"]?.GetValue<string>() != session.PrepareId)
            throw new InvalidOperationException("The ordered source belongs to a different preparation.");
        var frames = FrameBatch(response);
        if (frames.Count != 1 || frames[0].Cursor != 1 || frames[0].PhysicsTimeSeconds != sample.SimTimeSeconds ||
            frames[0].CommandDegrees.Zip(sample.CommandDegrees, (left, right) => Math.Abs(left - right)).Any(error => error > 1e-7) ||
            Math.Abs(frames[0].CommandOpeningMm - sample.CommandOpeningMm) > 1e-6)
            throw new InvalidOperationException("The ordered source did not retain the exact prepared first frame.");
        session.Ordered.Path.Append(frames);
        session.LastSample = pinned;
    }

    private static IReadOnlyList<OrderedSimulationFrame> FrameBatch(JsonObject response)
    {
        if (response["frames"] is not JsonArray frames || frames.Count > 128 || frames.Any(row => row is not JsonObject))
            throw new InvalidOperationException("The ordered frame response is incomplete or exceeds its bounded batch.");
        return frames.OfType<JsonObject>().Select(OrderedSimulationFrame.Read).ToArray();
    }

    private async Task ExecuteOrderedSource(SimulationSession session, JsonObject raw, SimulationFollowSample sample,
        SimulationFollowHardware hardware, double roundTrip, CancellationToken cancellation)
    {
        var execution = session.Ordered ?? throw new InvalidOperationException("No ordered source was prepared.");
        bool paused = raw["sourcePausedForFollower"]?.GetValue<bool>() == true;
        string episode = raw["episodeId"]?.GetValue<string>() ?? "";
        session.SourceObservation!.Validate(sample, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), roundTrip, paused,
            raw["episodeComplete"]?.GetValue<bool>() == true, episode, session.AcknowledgedEpisode.Length > 0);
        _ = hardware; // Hardware freshness/ownership is independently checked on every physical publication.
        var frames = FrameBatch(raw);
        int count;
        OrderedSimulationFrame? complete;
        bool missedGrasp;
        lock (session.OrderedGate)
        {
            if (!ulong.TryParse(raw["oldestCursor"]?.GetValue<string>(), out ulong oldest) || !ulong.TryParse(raw["newestCursor"]?.GetValue<string>(), out ulong newest) ||
                newest < execution.Path.LastCursor || frames.Count > 0 && oldest > execution.Path.LastCursor + 1)
                throw new InvalidOperationException("The ordered tape lost or rewound an unconsumed source cursor.");
            if (frames.Count > 0 && execution.Path.LastSource is { EpisodeComplete: true, EpisodeId.Length: > 0 } previous &&
                frames[0].EpisodeId != previous.EpisodeId &&
                (frames[0].CommandDegrees.Zip(execution.Target.JointDegrees, (target, prior) => Math.Abs(target - prior)).Any(error => error > .25) ||
                    Math.Abs(frames[0].CommandOpeningMm - execution.Target.OpeningMm) > 2))
                throw new InvalidOperationException("The next episode start differs from the completed home pose. Its first target was not sent.");
            execution.Path.Append(frames);
            session.SourcePaused = paused;
            count = execution.Path.FrameCount;
            complete = execution.Path.Drained && execution.CompletedCursor == execution.Path.LastCursor ? execution.Path.LastSource : null;
            missedGrasp = execution.MissedGrasp;
            Volatile.Write(ref session.OrderedEnvelopeTicks, Stopwatch.GetTimestamp());
        }
        if (complete is { EpisodeComplete: true, EpisodeId.Length: > 0 } && missedGrasp)
        {
            var receipt = telemetry.Latest!;
            var finalHardware = SimulationHardware(session, nativeReference: true, sourceReceipt: receipt);
            double speed = receipt.State["pika"]?["telemetry"]?["motor"]?["Speed"]?.GetValue<double>() ?? double.NaN;
            lock (session.OrderedGate)
                if (!execution.ReturnedHome(finalHardware, SimulationEndpointStationary.IsStationary(receipt, session.Backend,
                    session.Robot.DeviceId, session.Robot.Generation, Stopwatch.GetTimestamp()), speed))
                    throw new InvalidOperationException("The missed-grasp cycle ended without verified zero joints and its settled opened-jaw reference. Following stopped.");
            // A missed trial must never acknowledge success or release another
            // randomized episode. Normal scoped holds keep motor power intact.
            await StopSimulationFollowing(session, "Grasp not confirmed · cycle finished at home · holding", cancellation, fromWorker: true);
            return;
        }
        if (complete is { EpisodeComplete: true, EpisodeSucceeded: true, EpisodeId.Length: > 0 } && session.AcknowledgedEpisode != complete.EpisodeId)
        {
            session.Operation = "ack-episode:physical-arrival";
            await ControlSimulation(session, "ack-episode", cancellation, properties: new JsonObject
            {
                ["episodeId"] = complete.EpisodeId, ["episodeOrdinal"] = complete.EpisodeOrdinal,
                ["finalFrameCursor"] = complete.Cursor.ToString(CultureInfo.InvariantCulture)
            });
            session.AcknowledgedEpisode = complete.EpisodeId;
        }
        if (!paused && count >= OrderedSimulationPath.HighWaterFrames)
            await ControlSimulation(session, "pause-follower-source", cancellation);
        else if (paused && count <= OrderedSimulationPath.LowWaterFrames)
            await ControlSimulation(session, "resume-follower-source", cancellation);
    }

    private async Task PublishOrderedTargets(SimulationSession session)
    {
        var cadence = new SimulationPublishCadence(Stopwatch.GetTimestamp(), Stopwatch.Frequency, TimeSpan.FromMilliseconds(30));
        SimulationFollowHardware? observedHardware = null;
        bool observedStationary = false;
        double observedSpeed = double.NaN, observedFeedback = double.NaN;
        try
        {
            while (!session.Stop.IsCancellationRequested)
            {
                observedHardware = null;
                var receipt = telemetry.Latest!;
                var hardware = SimulationHardware(session, nativeReference: true, sourceReceipt: receipt);
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref session.OrderedEnvelopeTicks)).TotalMilliseconds > 250)
                    throw new InvalidOperationException("The ordered source observation expired after 250 ms; queued targets were retired.");
                double speed = receipt.State["pika"]?["telemetry"]?["motor"]?["Speed"]?.GetValue<double>() ?? double.NaN;
                double feedback = receipt.State["pika"]?["feedback_time_s"]?.GetValue<double>() ?? double.NaN;
                observedHardware = hardware;
                observedStationary = SimulationEndpointStationary.IsStationary(receipt, session.Backend, session.Robot.DeviceId,
                    session.Robot.Generation, Stopwatch.GetTimestamp());
                observedSpeed = speed; observedFeedback = feedback;
                OrderedSimulationTarget target;
                lock (session.OrderedGate)
                {
                    var execution = session.Ordered!;
                    target = execution.Step(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, hardware,
                        observedStationary, speed, feedback);
                    simulationErrorDegrees = hardware.NativeReference!.JointDegrees.Zip(hardware.JointDegrees, (transmitted, real) => Math.Abs(transmitted - real)).Max();
                    double pathLagDegrees = target.JointDegrees.Zip(hardware.JointDegrees, (planned, real) => Math.Abs(planned - real)).Max();
                    simulationGripperErrorMm = execution.ActivePhase is not null ? Math.Abs(execution.ExpectedMeasuredOpeningMm - hardware.GripperOpeningMm) : null;
                    string prefix = execution.MissedGrasp ? "Grasp not confirmed · continuing the cycle to home" :
                        execution.Path.SourceComplete && !execution.Path.Drained ? "Simulation finished · waiting for real robot" : "Following ordered path";
                    simulationStatus = execution.ActivePhase is { } active
                        ? active.Kind == OrderedSimulationPhaseKind.Hold
                            ? $"{ActiveProfile.Name} is holding · waiting for its next arm or jaw command · simulation is running"
                            : $"{prefix} · {active.Kind.ToString().ToLowerInvariant()} {Math.Min(execution.ElapsedSeconds, active.DurationSeconds):F1}/{active.DurationSeconds:F1} s · {execution.Path.FrameCount} frames buffered · native tracking {simulationErrorDegrees:F2}° · planned path lag {pathLagDegrees:F2}°"
                        : execution.Path.Drained ? "Episode complete · real robot settled · waiting for the next episode"
                        : "Buffering the next arm or jaw phase · real robot holds its last pose";
                    simulationDetail = $"Only {ActiveProfile.Name} is following its assigned sim arm. Other physical arms do not start automatically. A relay can hold this arm while another simulated arm performs its part. Solid: simulation leader; transparent: measured real robot. Jaw events wait for the preceding arm phase, and the next episode waits until this real arm and Pika finish.";
                }
                await SendSimulationTarget(session, target.JointDegrees, target.OpeningMm, session.Stop.Token);
                await Task.Delay(cadence.DelayAfterPublication(Stopwatch.GetTimestamp()), session.Stop.Token);
            }
        }
        catch (OperationCanceledException) when (session.Stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            SimulationFollowFailureOrdered? ordered = null;
            try
            {
                lock (session.OrderedGate)
                    if (observedHardware is not null && session.Ordered is { } execution)
                        ordered = SimulationFollowFailureOrdered.Capture(execution, observedHardware, observedStationary, observedSpeed, observedFeedback);
            }
            catch (Exception diagnosticError) { Trace.TraceWarning("Could not capture ordered phase diagnostics: {0}", diagnosticError.GetType().Name); }
            RecordSimulationFailure(session, error, ordered);
            session.Stop.Cancel();
        }
    }
}

namespace LuckyRobots.Piper.Plugin;

/// <summary>Physical progress follows a closed continuous phase, independently
/// of the source renderer's cadence. Endpoint barriers order every jaw event.</summary>
public sealed class OrderedSimulationExecution
{
    public OrderedSimulationPath Path { get; }
    public OrderedSimulationPhase? ActivePhase { get; private set; }
    public OrderedSimulationTarget Target { get; private set; }
    public ulong CompletedCursor { get; private set; }
    public double ElapsedSeconds { get; private set; }
    private double phaseBegan, settledBegan = double.NaN, settledFeedback;
    private double lastNow = double.NaN;
    private double expectedOpening;
    private double? qualifiedContactOpening, resolvedOpening, resolvedCommand;
    private bool settledUnconfirmedCandidate;
    private ulong completedHomeCursor;
    private string completedHomeEpisode = "";
    private double? lastClosingCommand, completedOpeningCommand;
    private double contactLossBegan = double.NaN, contactLossMinimum, contactLossCommand;
    private double contactLossSettledBegan = double.NaN, contactLossSettledFeedback;
    public bool QualifyingLostContact => !double.IsNaN(contactLossBegan);
    public bool PhysicalGraspMissed { get; private set; }
    public bool MissedGrasp => PhysicalGraspMissed || Path.SourceMissedGrasp;
    public double? QualifiedContactOpeningMm => qualifiedContactOpening;
    public double ExpectedMeasuredOpeningMm => resolvedOpening ?? (ActivePhase is { } phase && Opening(phase)
        ? phase.End.CommandOpeningMm : ActivePhase?.ExpectedMeasuredOpeningMm ?? expectedOpening);
    public bool RequiresPhysicalContactCandidate => ActivePhase is { } phase && ClosingContact(phase);
    public double OpeningArrivalToleranceMm => RequiresPhysicalContactCandidate ? 5 : 2;
    private long nativeRequest, nativeSequence;
    private readonly string limits;

    public OrderedSimulationExecution(SimulationFollowSample first, SimulationFollowHardware hardware, double selectedSpeedFraction)
    {
        Path = new(hardware.JointLimits, selectedSpeedFraction);
        Target = new(Array.AsReadOnly(first.CommandDegrees.ToArray()), first.CommandOpeningMm);
        expectedOpening = first.MeasuredOpeningMm;
        limits = Signature(hardware);
    }

    public OrderedSimulationTarget Step(double nowSeconds, SimulationFollowHardware hardware, bool armStationary,
        double gripperSpeed, double gripperFeedbackTime, bool startNextPhase = true)
    {
        if (!double.IsFinite(nowSeconds) || !double.IsFinite(gripperSpeed) || !double.IsFinite(gripperFeedbackTime) ||
            !double.IsNaN(lastNow) && nowSeconds < lastNow || Signature(hardware) != limits || !hardware.FeedbackFresh || !hardware.GripperFresh || !hardware.LimitsFresh ||
            hardware.JointDegrees.Count != 6 || hardware.JointDegrees.Any(q => !double.IsFinite(q)) || !double.IsFinite(hardware.GripperOpeningMm))
            throw new InvalidOperationException("The physical follower clock, connection or reported limits changed.");
        var reference = hardware.NativeReference;
        if (reference is null || reference.DeviceId != hardware.DeviceId || reference.Generation != hardware.Generation || reference.RequestId <= 0 ||
            nativeRequest != 0 && reference.RequestId != nativeRequest || reference.Sequence < 0 || nativeRequest != 0 && reference.Sequence < nativeSequence ||
            reference.JointDegrees.Count != 6 || reference.JointDegrees.Where((q, joint) => !double.IsFinite(q) ||
                q < hardware.JointLimits[joint].MinimumDegrees || q > hardware.JointLimits[joint].MaximumDegrees).Any())
            throw new InvalidOperationException("The scoped native trajectory reference is unavailable or changed; following stopped.");
        nativeRequest = reference.RequestId; nativeSequence = reference.Sequence;
        lastNow = nowSeconds;
        if (startNextPhase && ActivePhase is null && Path.Current is { } next)
        {
            if (QualifyingLostContact && (next.Kind == OrderedSimulationPhaseKind.Gripper || next.Frames.Any(frame => frame.CommandOpeningMm != contactLossCommand)))
                throw new InvalidOperationException("The jaw command changed before lost contact could be verified; following stopped.");
            // A contact baseline belongs to exactly one closed jaw command.
            // A new jaw operation must qualify its own measured endpoint.
            if (next.Kind == OrderedSimulationPhaseKind.Gripper || resolvedCommand is { } command && next.Frames.Any(frame => frame.CommandOpeningMm != command))
                qualifiedContactOpening = resolvedOpening = resolvedCommand = null;
            if (next.Kind == OrderedSimulationPhaseKind.Gripper)
            {
                completedOpeningCommand = null;
                if (next.End.CommandOpeningMm < next.Frames[0].CommandOpeningMm) lastClosingCommand = next.End.CommandOpeningMm;
            }
            ActivePhase = next; phaseBegan = nowSeconds; settledBegan = double.NaN;
        }
        var phase = ActivePhase;
        if (phase is not null) { ElapsedSeconds = nowSeconds - phaseBegan; Target = phase.At(ElapsedSeconds); }
        if (resolvedCommand is { } qualifiedCommand && Target.OpeningMm != qualifiedCommand)
            qualifiedContactOpening = resolvedOpening = resolvedCommand = null;
        for (int joint = 0; joint < 6; joint++)
            if (Math.Abs(reference.JointDegrees[joint] - hardware.JointDegrees[joint]) > SimulationFollowPolicy.TrackingJointToleranceDegrees)
                throw new InvalidOperationException($"Real J{joint + 1} is more than 5° from the controller's transmitted reference. Following stopped.");
        UpdateContactLoss(nowSeconds, hardware.GripperOpeningMm, gripperSpeed, gripperFeedbackTime);
        double expected = ExpectedMeasuredOpeningMm;
        double sourceExpected = phase?.ExpectedMeasuredOpeningMm ?? expectedOpening;
        double low = sourceExpected, high = sourceExpected;
        if (phase is { Kind: OrderedSimulationPhaseKind.Gripper })
        {
            low = phase.Frames.Min(f => Math.Min(f.MeasuredOpeningMm, f.CommandOpeningMm));
            high = phase.Frames.Max(f => Math.Max(f.MeasuredOpeningMm, f.CommandOpeningMm));
        }
        else if (QualifyingLostContact)
        {
            low = contactLossCommand - Pika.PikaProtocol.ArrivalToleranceMm;
            high = qualifiedContactOpening!.Value;
        }
        else if (MissedGrasp && resolvedOpening is { } physicalReference && resolvedCommand == Target.OpeningMm)
        {
            // An explicitly missed trial can have empty real jaws while the
            // simulation still has contact, or the reverse. Its settled,
            // unchanged physical jaw reference remains authoritative while
            // finishing the original path. The stricter2mm continuity guard
            // below still detects any actual change from that reference.
            low = high = physicalReference;
        }
        if (hardware.GripperOpeningMm < low - SimulationFollowPolicy.TrackingGripperToleranceMm || hardware.GripperOpeningMm > high + SimulationFollowPolicy.TrackingGripperToleranceMm)
            throw new InvalidOperationException("Real Pika is more than 10 mm from its retimed jaw phase. Following stopped.");
        if (!QualifyingLostContact && resolvedOpening is { } settledOpening && Math.Abs(hardware.GripperOpeningMm - settledOpening) > 2)
            throw new InvalidOperationException("Pika's measured opening changed more than 2 mm from its settled jaw reference. Following stopped.");
        if (phase is null) return Target;
        // A closing command intentionally can lead its measured contact opening.
        // Opening resolves to its command; neither aperture check proves a grasp.
        bool unconfirmedClose = ClosingContact(phase) && UnconfirmedOpening(hardware.GripperOpeningMm, phase.End.CommandOpeningMm);
        bool arrived = !QualifyingLostContact && ElapsedSeconds >= phase.DurationSeconds && armStationary &&
            Target.JointDegrees.Zip(hardware.JointDegrees, (target, actual) => Math.Abs(target - actual)).All(error => error <= .25) &&
            Math.Abs(gripperSpeed) <= .02 && (unconfirmedClose ||
                Math.Abs(hardware.GripperOpeningMm - expected) <= OpeningArrivalToleranceMm &&
                (!RequiresPhysicalContactCandidate || hardware.GripperOpeningMm - phase.End.CommandOpeningMm > 2));
        if (!arrived) settledBegan = double.NaN;
        else if (double.IsNaN(settledBegan) || settledUnconfirmedCandidate != unconfirmedClose)
        { settledBegan = nowSeconds; settledFeedback = gripperFeedbackTime; settledUnconfirmedCandidate = unconfirmedClose; }
        else if (nowSeconds - settledBegan >= .1 && gripperFeedbackTime > settledFeedback)
        {
            if (unconfirmedClose)
            {
                // The same>2mm separation required to qualify contact leaves
                // every smaller gap unconfirmed, including1–2mm servo error.
                // Keep the existing source cycle to home without claiming a
                // grasp or demanding an unreachable exact close endpoint.
                PhysicalGraspMissed = true;
                qualifiedContactOpening = null;
                resolvedOpening = hardware.GripperOpeningMm;
                resolvedCommand = phase.End.CommandOpeningMm;
            }
            else if (ClosingContact(phase))
            {
                // A bounded5mm initial model-agreement window accommodates soft
                // contact differences; both source and actual aperture must be
                // strictly>command+2mm. Fresh settled samples still apply.
                // Preserve actual aperture for this exact command;
                // variation in soft simulated contact must not demand squeezing
                // the real object further. Aperture alone does not prove a grasp.
                qualifiedContactOpening = hardware.GripperOpeningMm;
                resolvedOpening = hardware.GripperOpeningMm;
                resolvedCommand = phase.End.CommandOpeningMm;
            }
            else if (Opening(phase))
            {
                // An opening controller must arrive at its commanded endpoint,
                // not remain at transient simulated servo lag. This is a jaw
                // reference only, never contact or secure-grasp evidence.
                resolvedOpening = phase.End.CommandOpeningMm;
                resolvedCommand = phase.End.CommandOpeningMm;
                completedOpeningCommand = phase.End.CommandOpeningMm;
            }
            if (OrderedSimulationPath.PhaseKind(phase.End.Phase) == "home")
            { completedHomeCursor = phase.End.Cursor; completedHomeEpisode = phase.End.EpisodeId; }
            Path.Complete(phase); CompletedCursor = phase.End.Cursor; expectedOpening = phase.ExpectedMeasuredOpeningMm; ActivePhase = null;
        }
        if (ElapsedSeconds > phase.DurationSeconds + 15)
            throw new InvalidOperationException("The real arm or Pika did not settle at its ordered phase endpoint; the next phase was not started.");
        return Target;
    }

    private void UpdateContactLoss(double now, double opening, double speed, double feedbackTime)
    {
        if (!QualifyingLostContact)
        {
            if (qualifiedContactOpening is not { } contact || resolvedCommand is not { } command || command != Target.OpeningMm ||
                command >= contact || opening >= contact - 2 || opening < command - Pika.PikaProtocol.ArrivalToleranceMm)
                return;
            contactLossBegan = now; contactLossMinimum = opening; contactLossCommand = command;
            contactLossSettledBegan = double.NaN;
        }
        if (resolvedCommand != contactLossCommand || Target.OpeningMm != contactLossCommand || qualifiedContactOpening is null)
            throw new InvalidOperationException("The jaw command changed before lost contact could be verified; following stopped.");
        if (now - contactLossBegan >= .250)
            throw new InvalidOperationException("Pika did not settle at its unchanged close command within250ms after contact changed; following stopped.");
        if (speed > .02 || opening > contactLossMinimum + 2 || opening < contactLossCommand - Pika.PikaProtocol.ArrivalToleranceMm)
            throw new InvalidOperationException("Pika reversed away from its close endpoint or exceeded that endpoint while checking lost contact; following stopped.");
        contactLossMinimum = Math.Min(contactLossMinimum, opening);
        bool settled = UnconfirmedOpening(opening, contactLossCommand) && Math.Abs(speed) <= .02;
        if (!settled) contactLossSettledBegan = double.NaN;
        else if (double.IsNaN(contactLossSettledBegan))
        { contactLossSettledBegan = now; contactLossSettledFeedback = feedbackTime; }
        else if (now - contactLossSettledBegan >= .1 && feedbackTime > contactLossSettledFeedback)
        {
            PhysicalGraspMissed = true;
            qualifiedContactOpening = null;
            resolvedOpening = opening; resolvedCommand = contactLossCommand;
            contactLossBegan = contactLossSettledBegan = double.NaN;
        }
    }

    private static bool UnconfirmedOpening(double opening, double command) =>
        opening >= command - Pika.PikaProtocol.ArrivalToleranceMm && opening <= command + 2;

    public bool ReturnedHome(SimulationFollowHardware hardware, bool armStationary, double gripperSpeed) =>
        Path.Drained && CompletedCursor == Path.LastCursor && OrderedSimulationPath.PhaseKind(Path.LastSource?.Phase ?? "") == "finish" &&
        completedHomeCursor > 0 && completedHomeEpisode == Path.LastSource?.EpisodeId &&
        hardware.FeedbackFresh && hardware.GripperFresh && armStationary && double.IsFinite(gripperSpeed) && Math.Abs(gripperSpeed) <= .02 &&
        Target.JointDegrees.All(q => Math.Abs(q) <= .25) && hardware.JointDegrees.All(q => Math.Abs(q) <= .25) &&
        completedOpeningCommand is { } opened && lastClosingCommand is { } closed && opened > closed && Target.OpeningMm == opened &&
        resolvedOpening == opened && Math.Abs(hardware.GripperOpeningMm - opened) <= 2;

    private static bool Opening(OrderedSimulationPhase phase) => phase.Kind == OrderedSimulationPhaseKind.Gripper &&
        phase.End.CommandOpeningMm > phase.Frames[0].CommandOpeningMm;
    private static bool ClosingContact(OrderedSimulationPhase phase) => phase.Kind == OrderedSimulationPhaseKind.Gripper &&
        phase.End.CommandOpeningMm < phase.Frames[0].CommandOpeningMm && phase.ExpectedMeasuredOpeningMm - phase.End.CommandOpeningMm > 2;

    private static string Signature(SimulationFollowHardware value) => value.DeviceId + "/" + value.Generation + "/" + string.Join(";",
        value.JointLimits.OrderBy(j => j.JointNumber).Select(j => FormattableString.Invariant($"{j.JointNumber},{j.MinimumDegrees:R},{j.MaximumDegrees:R},{j.MaximumDegreesPerSecond:R},{j.MaximumDegreesPerSecondSquared:R},{j.AccelerationFresh}")));
}

/// <summary>Strict source-envelope ordering without a wall-poll derivative.
/// Physics tape nodes, rather than RPC sampling intervals, define the path.</summary>
public sealed class OrderedSimulationObservation
{
    private readonly SimulationFollowSample first;
    private long sequence, captured, scene;
    private double physics;
    private long lastProgressCapture;
    private string episode = "";
    public OrderedSimulationObservation(SimulationFollowSample initial)
    {
        first = initial; sequence = initial.Sequence; captured = lastProgressCapture = initial.CapturedMonotonicNs;
        scene = initial.SimulationTimeNs; physics = initial.SimTimeSeconds;
    }
    public void Validate(SimulationFollowSample sample, long receivedAtUnixMilliseconds, double roundTripMilliseconds,
        bool sourcePaused, bool sourceComplete, string episodeId, bool previousEpisodeAcknowledged)
    {
        double age = receivedAtUnixMilliseconds - sample.CapturedAtUnixMicroseconds / 1000d;
        if (!sample.BindsTo(first) || sample.SceneId != sample.RunId || sample.Mode is not ("play" or "pause") || sample.Mode == "pause" && !sourcePaused ||
            !double.IsFinite(roundTripMilliseconds) || roundTripMilliseconds is < 0 or > 150 || age is < -50 or > 250 ||
            sample.Sequence <= sequence || sample.CapturedMonotonicNs <= captured || sample.TimeScale <= 0 || sample.TimeScale > 1.000001)
            throw new InvalidOperationException("The ordered simulation envelope is stale, paused manually, out of order or changed scope.");
        bool newEpisode = episodeId != episode;
        bool permittedReset = newEpisode && (episode.Length == 0 || previousEpisodeAcknowledged);
        if ((sample.SimulationTimeNs < scene || sample.SimTimeSeconds < physics) && !permittedReset)
            throw new InvalidOperationException("The ordered simulation clock reset without a completed episode acknowledgement.");
        if (sample.SimTimeSeconds != physics || permittedReset) lastProgressCapture = sample.CapturedMonotonicNs;
        else if (!sourcePaused && !sourceComplete && sample.CapturedMonotonicNs - lastProgressCapture > 250_000_000L)
            throw new InvalidOperationException("Ordered source physics did not advance within 250 ms.");
        sequence = sample.Sequence; captured = sample.CapturedMonotonicNs; scene = sample.SimulationTimeNs; physics = sample.SimTimeSeconds; episode = episodeId;
    }
}

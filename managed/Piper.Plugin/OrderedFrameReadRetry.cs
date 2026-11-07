namespace LuckyRobots.Piper.Plugin;

/// <summary>Public retained-frame retry policy shared by the sole follower
/// coordinator and offline protocol fixtures. A retry grants no fresh lease.</summary>
public static class OrderedFrameReadRetry
{
    public static bool ExpectedStop(bool attemptDeadlineExpired, bool stopRequested, bool ending) =>
        !attemptDeadlineExpired && stopRequested && ending;

    public static bool Allowed(double validatedEnvelopeAgeMilliseconds, bool cancellationRequested, bool sameScope, bool publisherRunning) =>
        !cancellationRequested && sameScope && publisherRunning && double.IsFinite(validatedEnvelopeAgeMilliseconds) &&
        validatedEnvelopeAgeMilliseconds >= 0 && validatedEnvelopeAgeMilliseconds < SimulationFollowPolicy.MaximumSourceAgeMilliseconds;
}

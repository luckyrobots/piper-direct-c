using System.Diagnostics;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Cal-D: the jaw-height check. <see cref="HeightSession.Frozen"/> is the pinned pose.
/// <code>
///   Inactive -> Live | Pinned | Saved   Check jaw height (a restored draft pose resumes)
///   Live -> Pinned                      Measure this pose, or a first tape entry
///   Pinned|Saved -> Pinned              Measure this pose again
///   Pinned -> Saved                     Save check
///   Pinned|Saved -> Live                Check another pose
///   any -> Inactive                     Stop height check, or a profile change
/// </code>
/// Live has no pinned pose; Pinned and Saved have one. Inactive may keep the
/// saved draft pose so the next Check jaw height resumes it.</summary>
internal enum HeightCheckPhase { Inactive, Live, Pinned, Saved }

/// <summary>Everything the operator's height-check actions decide, as one value.</summary>
internal sealed record HeightSession(HeightCheckPhase Phase, string Profile, HeightObservation? Frozen, string MeasuredText,
    string Status, string ActionError, bool StorageBlocked, HeightCheck[] Checks)
{
    public const string Prompt = "Move to a few different heights and compare them with a tape measure.";
    public static readonly HeightSession Unloaded = new(HeightCheckPhase.Inactive, "", null, "", Prompt, "", false, []);

    /// <summary>A profile's session before its saved checks are read.</summary>
    public static HeightSession For(string profile) => Unloaded with { Profile = profile };

    public bool Active => Phase != HeightCheckPhase.Inactive;
    /// <summary>The pinned pose is one of the saved checks.</summary>
    public bool FrozenInChecks => Frozen is { } frozen && Checks.Any(check =>
        check.Observation.CapturedAt == frozen.CapturedAt && check.Observation.Capture.PoseFingerprint == frozen.Capture.PoseFingerprint);
    public bool FrozenSaved => Phase == HeightCheckPhase.Saved || Phase == HeightCheckPhase.Inactive && FrozenInChecks;
    /// <summary>The phase Check jaw height resumes in.</summary>
    public HeightCheckPhase ResumePhase => Frozen is null ? HeightCheckPhase.Live : FrozenInChecks ? HeightCheckPhase.Saved : HeightCheckPhase.Pinned;

    public bool Allows(HeightCheckPhase next) => next switch
    {
        HeightCheckPhase.Live => Frozen is null,
        HeightCheckPhase.Pinned => Frozen is not null && !FrozenInChecks,
        HeightCheckPhase.Saved => Frozen is not null && FrozenInChecks,
        _ => true
    };
}

/// <summary>One live prediction of the jaw height, with the clock reading it was taken at.</summary>
internal sealed record LiveHeightPreview(HeightObservation Observation, long ReadAtTicks, double SourceAgeMilliseconds);

/// <summary>What the telemetry loop last learned for one run of the check: a preview, or why there is none.</summary>
internal sealed record HeightReading(long Epoch, LiveHeightPreview? Preview, string Issue)
{
    public static readonly HeightReading None = new(0, null, "");
}

/// <summary>The Cal-D part of the Calibration component.
/// <code>
///   field        written by                                         readers see
///   session      the UI actor (every transition below) and the      one volatile reference to an
///                render path (LoadProfile). Each write is a         immutable HeightSession
///                compare-and-swap from the value its writer read.
///   epoch        the UI actor, once per start, stop and profile     interlocked read
///                load
///   reading      the telemetry loop (Observe)                       one volatile reference; a reading
///                                                                   from an older epoch reads as None
///   guideIssue   the maintenance task (ReportGuide)                 the same, as ""
///   guideAt      the maintenance task (MarkGuideAttempt)            interlocked read
///   guideShown   the maintenance task (MarkGuideShown) and whoever  interlocked read
///                clears the line (TakeGuideShown); both exchange
///   maintenance  the telemetry loop (QueueMaintenance)              one volatile reference to a task
///                                                                   that never faults (B12)
/// </code>
/// A start, a stop and a profile load do not clear what the telemetry loop and
/// the maintenance task wrote; they advance the epoch, and values stamped with
/// an older epoch stop being read. No transition takes a lock.
///
/// Not owned here: the saved file (the workflow reads and writes it), the
/// desk session the check validates against, and the engine's height line.</summary>
internal sealed class HeightCheckSession
{
    private sealed record GuideIssue(long Epoch, string Text);

    private HeightSession session = HeightSession.Unloaded;
    private HeightReading reading = HeightReading.None;
    private GuideIssue guideIssue = new(0, "");
    private long epoch, guideAt;
    private int guideShown;
    private Task? maintenance;

    public HeightSession Session => Volatile.Read(ref session);
    public bool Active => Session.Active;
    /// <summary>Advances on every start, stop and profile load.</summary>
    public long Epoch => Interlocked.Read(ref epoch);
    /// <summary>The telemetry loop's latest reading for the current epoch.</summary>
    public HeightReading Reading => Volatile.Read(ref reading) is { } current && current.Epoch == Epoch ? current : HeightReading.None;
    /// <summary>Why the height line is unavailable in the current epoch, or "".</summary>
    public string GuideProblem => Volatile.Read(ref guideIssue) is { } issue && issue.Epoch == Epoch ? issue.Text : "";
    public long GuideAttemptedAt => Interlocked.Read(ref guideAt);
    public bool GuideShown => Volatile.Read(ref guideShown) != 0;
    /// <summary>For shutdown (B12): completes when no height-line request is in flight. Never faults.</summary>
    public Task MaintenanceIdle => Volatile.Read(ref maintenance) ?? Task.CompletedTask;

    private HeightSession Change(Func<HeightSession, HeightSession> change)
    {
        HeightSession current, next;
        do { current = Session; next = change(current); }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref session, next, current), current));
        return next;
    }

    // ---- the operator's session ----

    /// <summary>Replaces the session with <paramref name="read"/>'s when
    /// <paramref name="profile"/> is not the loaded one. The check closes.</summary>
    public void LoadProfile(string profile, Func<HeightSession> read)
    {
        while (Session is { } current && current.Profile != profile)
        {
            var loaded = read() with { Profile = profile, Phase = HeightCheckPhase.Inactive };
            if (!ReferenceEquals(Interlocked.CompareExchange(ref session, loaded, current), current)) continue;
            Interlocked.Increment(ref epoch);
            return;
        }
    }

    /// <summary>Enters <paramref name="next"/> when the pinned pose agrees with it.
    /// A phase that disagrees would show a measurement for the wrong pose, so
    /// the check closes instead and tells the operator. False when it closed.</summary>
    public bool Enter(HeightCheckPhase next)
    {
        bool entered = Change(current => Entering(current, next)).Phase == next || next == HeightCheckPhase.Inactive;
        if (!entered) Trace.TraceError("Jaw-height phase {0} disagrees with its pinned pose.", next);
        return entered;
    }

    private static HeightSession Entering(HeightSession current, HeightCheckPhase next) =>
        current.Allows(next) ? current with { Phase = next } : current with { Phase = HeightCheckPhase.Inactive,
            ActionError = "The jaw-height check lost track of its pinned pose and was closed. Nothing was saved; start Check jaw height again." };

    /// <summary>Check jaw height: resumes at the pinned pose, if one was kept.</summary>
    public void Start(string status)
    {
        // The epoch advances first, so a reading taken once the check is open belongs to it.
        Interlocked.Increment(ref epoch);
        Change(current => current with { Phase = current.ResumePhase, Status = current.StorageBlocked ? current.Status : status });
    }

    /// <summary>Stop height check. The pinned draft pose is kept.</summary>
    public void Stop()
    {
        Enter(HeightCheckPhase.Inactive);
        Interlocked.Increment(ref epoch);
    }

    /// <summary>Pins <paramref name="frozen"/> as the pose being measured.</summary>
    public void Pin(HeightObservation frozen, string status)
    {
        // One publication: no reader sees a pinned pose in Live, or the reverse.
        Change(current => Entering(current with { Frozen = frozen, Status = status }, HeightCheckPhase.Pinned));
    }

    /// <summary>Check another pose: drops the pinned pose and its tape entry.</summary>
    public void Unpin(string status)
    {
        Change(current => Entering(current with { Frozen = null, MeasuredText = "", Status = status }, HeightCheckPhase.Live));
    }

    public void EnterTape(string text) => Change(current => current with { MeasuredText = text });

    /// <summary>Replaces the saved checks; the caller passes the previous array back when its save fails.</summary>
    public void ReplaceChecks(HeightCheck[] checks) => Change(current => current with { Checks = checks });

    public void Report(string status) => Change(current => current with { Status = status });
    public void ReportActionError(string error) => Change(current => current with { ActionError = error });

    // ---- the telemetry loop's reading ----

    /// <summary>Records a reading taken while <paramref name="readEpoch"/> was current.
    /// A reading from an older epoch than the one held is dropped: it must not
    /// erase a current reading.</summary>
    public void Observe(long readEpoch, LiveHeightPreview? preview, string issue)
    {
        var next = new HeightReading(readEpoch, preview, issue);
        HeightReading current;
        do { current = Volatile.Read(ref reading); if (current.Epoch > readEpoch) return; }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref reading, next, current), current));
    }

    // ---- the engine's height line ----

    public void MarkGuideAttempt(long at) => Interlocked.Exchange(ref guideAt, at);
    public void MarkGuideShown() => Interlocked.Exchange(ref guideShown, 1);
    /// <summary>Marks the line as not shown; true when it had been.</summary>
    public bool TakeGuideShown() => Interlocked.Exchange(ref guideShown, 0) != 0;
    /// <summary>Records the height line's state for <paramref name="attemptEpoch"/>; an older epoch's report is dropped.</summary>
    public void ReportGuide(long attemptEpoch, string issue)
    {
        var next = new GuideIssue(attemptEpoch, issue);
        GuideIssue current;
        do { current = Volatile.Read(ref guideIssue); if (current.Epoch > attemptEpoch) return; }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref guideIssue, next, current), current));
    }

    /// <summary>Starts one maintenance pass unless one is still running.</summary>
    public void QueueMaintenance(Func<Task> pass)
    {
        if (Volatile.Read(ref maintenance) is { IsCompleted: false }) return;
        Volatile.Write(ref maintenance, pass());
    }
}

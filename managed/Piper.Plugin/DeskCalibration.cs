using System.Collections.Immutable;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Cal-A: the desk calibration session, derived from <see cref="DeskSession"/>.
/// <code>
///   None -> Capturing                start pins identity and original placement
///   Capturing -> Fitted              fit
///   Fitted -> Capturing              capture, correction or re-evaluation clears the fit
///   Fitted|Applied -> WritePending   apply or rollback sent; reply not verified yet
///   WritePending -> Applied          verified apply, or inspect finds the proposal
///   WritePending -> Fitted           verified rollback, a failed pre-send check, or inspect finds the original
///   any -> None                      new/archive (WritePending and Applied ask first) or a profile change
/// </code>
/// Illegal (see <see cref="DeskSession.Fault"/>): placement or samples without an
/// identity, or an applied placement without the fit that proposed it. Such a
/// session is never saved; New discards it and says so.</summary>
internal enum CalibrationPhase { None, Capturing, Fitted, WritePending, Applied }

/// <summary>Everything one robot profile's desk calibration remembers, as one value.</summary>
internal sealed record DeskSession(string Profile, string Path, CalibrationDesk Desk, CalibrationMeasurement Measurement,
    CalibrationIdentity? Identity, CalibrationPlacement? Original, CalibrationPlacement? Applied, CalibrationFit? Fit,
    ImmutableList<CalibrationSample> Samples, CalibrationModelReevaluation? Reevaluation, bool PlacementUnverified, bool ArchiveConfirmation)
{
    public static readonly DeskSession Unloaded = new("", "", new(), new(), null, null, null, null, [], null, false, false);

    public CalibrationPhase Phase =>
        Identity is null ? CalibrationPhase.None
        : PlacementUnverified ? CalibrationPhase.WritePending
        : Applied is not null ? CalibrationPhase.Applied
        : Fit is not null ? CalibrationPhase.Fitted
        : CalibrationPhase.Capturing;

    public string? Fault =>
        Identity is null && (Original is not null || Applied is not null || Fit is not null || PlacementUnverified || Samples.Count > 0)
            ? "Calibration session data exists without its identity."
        : Applied is not null && Fit is null ? "An applied placement lost the fit that proposed it."
        : null;

    /// <summary>The one exit to <see cref="CalibrationPhase.None"/>. Desk, point settings and report path stay.</summary>
    public DeskSession Cleared() => this with { Identity = null, Original = null, Applied = null, Fit = null, Reevaluation = null,
        Samples = [], PlacementUnverified = false, ArchiveConfirmation = false };

    /// <summary>This session resumed from a validated saved report.</summary>
    public DeskSession Restored(CalibrationReport report, string path) => this with
    {
        Identity = report.Identity, Original = report.OriginalPlacement, Applied = report.AppliedPlacement, Fit = report.Fit,
        Reevaluation = report.ModelReevaluation, Samples = [.. report.Samples], Desk = report.Desk, Path = path,
        Measurement = report.Samples.LastOrDefault()?.Measurement ?? Measurement, PlacementUnverified = report.RequiresInspection
    };
}

/// <summary>The Cal-A part of the Calibration component.
/// <code>
///   field    written by                                            readers see
///   session  the UI actor (every transition below) and the render  one volatile reference to an
///            path (LoadProfile). Each write is a compare-and-swap  immutable DeskSession whose
///            from the value its writer read.                       sample list never changes
/// </code>
/// A reader that needs several fields to agree reads <see cref="Session"/> once.
///
/// Not owned here: the report and settings files (the workflow reads and
/// writes them), the robot link the identity is compared with, and the scene
/// root the placement is written to.</summary>
internal sealed class DeskCalibration
{
    private DeskSession session = DeskSession.Unloaded;

    public DeskSession Session => Volatile.Read(ref session);

    private void Change(Func<DeskSession, DeskSession> change)
    {
        DeskSession current;
        do current = Session;
        while (!ReferenceEquals(Interlocked.CompareExchange(ref session, change(current), current), current));
    }

    /// <summary>Replaces the session with <paramref name="read"/>'s when
    /// <paramref name="profile"/> is not the loaded one.</summary>
    public void LoadProfile(string profile, Func<DeskSession> read)
    {
        while (Session is { } current && current.Profile != profile)
            if (ReferenceEquals(Interlocked.CompareExchange(ref session, read() with { Profile = profile }, current), current)) return;
    }

    /// <summary>Resumes from a saved report the caller has validated.</summary>
    public void Restore(CalibrationReport report, string path) => Change(current => current.Restored(report, path));

    /// <summary>New calibration, an archive, or a discarded inconsistent session.</summary>
    public void Clear() => Change(current => current.Cleared());

    /// <summary>Start: pins the identity and the placement the fit will be measured from.</summary>
    public void Begin(CalibrationIdentity identity, CalibrationPlacement original) => Change(current => current with
        { Identity = identity, Original = original, Applied = null, Fit = null, Samples = [], PlacementUnverified = false });

    /// <summary>Names the report file this session saves to.</summary>
    public void SaveTo(string path) => Change(current => current with { Path = path });

    public void UseDesk(CalibrationDesk desk) => Change(current => current with { Desk = desk });
    public void UseMeasurement(CalibrationMeasurement measurement) => Change(current => current with { Measurement = measurement });

    /// <summary>A captured point. Any fit is stale.</summary>
    public void AddSample(CalibrationSample sample) => Change(current => current with { Samples = current.Samples.Add(sample), Fit = null });

    /// <summary>Corrected ruler entries for a captured point. Any fit is stale.</summary>
    public void Correct(CalibrationSample sample, CalibrationSample correction) =>
        Change(current => current with { Samples = current.Samples.Replace(sample, correction, ReferenceEqualityComparer.Instance), Fit = null });

    /// <summary>The saved poses recomputed against a corrected model. Any fit is stale.</summary>
    public void Reevaluate(CalibrationIdentity identity, IEnumerable<CalibrationSample> samples, CalibrationModelReevaluation receipt) =>
        Change(current => current with { Identity = identity, Samples = [.. samples], Fit = null, Reevaluation = receipt });

    public void Fitted(CalibrationFit fit) => Change(current => current with { Fit = fit });

    /// <summary>A scene write is about to be sent; until verified, only inspection is allowed.</summary>
    public void MarkWritePending() => Change(current => current with { PlacementUnverified = true });
    /// <summary>The pre-send check failed: nothing was sent.</summary>
    public void MarkWriteNotSent() => Change(current => current with { PlacementUnverified = false });
    /// <summary>The scene's placement is known: a verified write or an inspection.</summary>
    public void PlacementVerified(CalibrationPlacement? applied) => Change(current => current with { Applied = applied, PlacementUnverified = false });

    public void AskArchive() => Change(current => current with { ArchiveConfirmation = true });
    public void CancelArchive() => Change(current => current with { ArchiveConfirmation = false });

    /// <summary>Returns to a session value held earlier: the caller's save of a
    /// change failed. Fixtures also stage a session through it.</summary>
    public void Reinstate(DeskSession earlier) => Volatile.Write(ref session, earlier);
}

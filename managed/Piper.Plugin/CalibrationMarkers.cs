namespace LuckyRobots.Piper.Plugin;

/// <summary>The Cal-C part of the Calibration component: which temporary
/// marker the engine is showing for calibration (<see cref="MarkerState"/>),
/// whether the desk point marks are published, and the retry latch.
/// <code>
///   field          written by                                        readers see
///   marker         the UI actor and the marker-maintenance task,     one volatile reference to an
///                  both inside Gate (Show, Move, UnacknowledgeEdge); immutable MarkerState
///                  whoever leaves calibration, outside Gate (Take,
///                  Hide). Show, Take and Hide exchange; every
///                  conditional move is a compare-and-swap.
///   attemptedAt    whoever holds Gate (Show, MarkAttempt)            interlocked read
///   sheetDirty     the UI actor (InvalidateSheet, ResetSheet) and    volatile read
///                  whoever holds Gate (BeginSheetPublish)
///   sheetAt        whoever holds Gate (BeginSheetPublish)            interlocked read
///   sheetPublished whoever holds Gate (MarkSheetPublished) and       volatile read
///                  whoever leaves calibration (TakeSheetPublished,
///                  ResetSheet); the leaver exchanges
///   retryRequired  whoever holds Gate (RequireRetry, ClearRetry)     volatile read
///                  and a profile load (ClearRetry)
/// </code>
/// <see cref="Gate"/> orders the marker requests sent to the engine. It guards
/// no field here: every write above is atomic without it, and the STOP path
/// never takes it.
///
/// Not owned here: the engine markers themselves (they expire natively), and
/// the operator message a failed marker request leaves (the wizard part).</summary>
internal sealed class CalibrationMarkers
{
    private MarkerState marker = MarkerState.None;
    private long attemptedAt, sheetAt;
    private int sheetDirty = 1, sheetPublished, retryRequired;

    /// <summary>One marker request to the engine at a time.</summary>
    public SemaphoreSlim Gate { get; } = new(1);

    public MarkerState Marker => Volatile.Read(ref marker);
    public WizardHighlightScope? Tool => (Marker as MarkerState.Tool)?.Scope;
    public WizardReferenceHighlightScope? Edge => (Marker as MarkerState.Edge)?.Scope;
    /// <summary>When the tool or edge marker was last requested.</summary>
    public long AttemptedAt => Interlocked.Read(ref attemptedAt);
    public bool SheetDirty => Volatile.Read(ref sheetDirty) != 0;
    public long SheetAttemptedAt => Interlocked.Read(ref sheetAt);
    public bool SheetPublished => Volatile.Read(ref sheetPublished) != 0;
    /// <summary>A failed or unsupported edge marker waits for the Retry action.</summary>
    public bool RetryRequired => Volatile.Read(ref retryRequired) != 0;

    // ---- the tool-site or desk-edge marker ----

    /// <summary>Makes <paramref name="next"/> the marker being requested at <paramref name="at"/>.</summary>
    public void Show(MarkerState next, long at)
    {
        Interlocked.Exchange(ref marker, next);
        MarkAttempt(at);
    }

    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/> unless another writer replaced it.</summary>
    public bool Move(MarkerState from, MarkerState to) => ReferenceEquals(Interlocked.CompareExchange(ref marker, to, from), from);

    /// <summary>The question, desk or robot changed: an acknowledged edge needs a new acknowledgement.</summary>
    public void UnacknowledgeEdge()
    {
        if (Marker is MarkerState.Edge { AcknowledgedAt: not 0 } edge) Move(edge, edge with { AcknowledgedAt = 0 });
    }

    public void Hide() => Interlocked.Exchange(ref marker, MarkerState.None);
    /// <summary>Hides the marker and returns the one that was showing.</summary>
    public MarkerState Take() => Interlocked.Exchange(ref marker, MarkerState.None);
    public void MarkAttempt(long at) => Interlocked.Exchange(ref attemptedAt, at);

    // ---- the desk point marks ----

    /// <summary>An entered measurement changed; the marks are republished on the next pass.</summary>
    public void InvalidateSheet() => Volatile.Write(ref sheetDirty, 1);
    public void BeginSheetPublish(long at)
    {
        Volatile.Write(ref sheetDirty, 0);
        Interlocked.Exchange(ref sheetAt, at);
    }
    public void MarkSheetPublished() => Volatile.Write(ref sheetPublished, 1);
    /// <summary>Marks the points as not published; true when they had been.</summary>
    public bool TakeSheetPublished() => Interlocked.Exchange(ref sheetPublished, 0) != 0;
    /// <summary>The model changed under the marks: nothing is published and everything is due.</summary>
    public void ResetSheet()
    {
        Volatile.Write(ref sheetDirty, 1);
        Volatile.Write(ref sheetPublished, 0);
    }

    // ---- the retry latch ----

    public void RequireRetry() => Volatile.Write(ref retryRequired, 1);
    public void ClearRetry() => Volatile.Write(ref retryRequired, 0);
}

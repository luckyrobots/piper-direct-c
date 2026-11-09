using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record WizardHighlightScope(string ProfileId, string DeviceId, long Generation, string BackendFingerprint,
    string SceneId, string RobotId, string ModelAssetId, string ModelRevision, string SiteName)
{
    public bool Matches(CalibrationIdentity live, string site, string asset, string revision, bool calibrationOpen) => calibrationOpen &&
        live.ProfileId == ProfileId && live.DeviceId == DeviceId && live.Generation == Generation && live.BackendSessionFingerprint == BackendFingerprint &&
        live.SceneId == SceneId && live.RobotId == RobotId && site == SiteName && asset == ModelAssetId && revision == ModelRevision;
    public JsonObject Request(bool visible) => new() { ["sceneId"] = SceneId, ["entityId"] = RobotId,
        ["expectedModelAssetId"] = ModelAssetId, ["expectedModelRevision"] = ModelRevision, ["siteName"] = SiteName,
        ["durationSeconds"] = 30, ["visible"] = visible };
}

/// <summary>
/// The one tool-site or desk-edge marker the engine shows for calibration.
///   None -> Tool(scope)              Show jaw tips
///   None|Tool -> Edge(scope, 0)      Show edge, waiting for the editor's acknowledgement
///   Edge(scope, 0) -> Edge(scope, t) the editor acknowledged the pointer at t
///   Edge(scope, t) -> Edge(scope, 0) the question, desk or robot changed
///   any -> None                      hidden, cleared, or its context changed
/// Desk point marks (sheetMarkers*) are separate engine markers and the
/// "retry required" latch is a separate UI rule; neither is part of this state.
/// </summary>
internal abstract record MarkerState
{
    public static readonly MarkerState None = new Hidden();
    public sealed record Hidden : MarkerState;
    public sealed record Tool(WizardHighlightScope Scope) : MarkerState;
    public sealed record Edge(WizardReferenceHighlightScope Scope, long AcknowledgedAt) : MarkerState;
}

public sealed partial class PipersPlugin
{
    private readonly SemaphoreSlim wizardHighlightGate = new(1);
    // Written only through SetMarker/MoveMarker; ClearWizardHighlight may run
    // outside wizardHighlightGate, so conditional moves compare and swap.
    internal MarkerState wizardMarker = MarkerState.None;
    private long wizardHighlightAttempt;

    private WizardHighlightScope? ToolMarker => (wizardMarker as MarkerState.Tool)?.Scope;
    private WizardReferenceHighlightScope? EdgeMarker => (wizardMarker as MarkerState.Edge)?.Scope;
    private void SetMarker(MarkerState next) => Interlocked.Exchange(ref wizardMarker, next);
    private bool MoveMarker(MarkerState from, MarkerState to) => ReferenceEquals(Interlocked.CompareExchange(ref wizardMarker, to, from), from);
    private void UnacknowledgeEdge()
    {
        if (wizardMarker is MarkerState.Edge { AcknowledgedAt: not 0 } edge) MoveMarker(edge, edge with { AcknowledgedAt = 0 });
    }

    private bool WizardHighlightCurrent(WizardHighlightScope scope)
    {
        try
        {
            _ = Device(scope.DeviceId, scope.Generation);
            return calibrationWizard is not null && scope.Matches(CurrentCalibrationIdentity(), Wizard.Answers.GetValueOrDefault("tool", "").Trim(),
                Wizard.ModelAssetId, Wizard.ModelRevision, calibrationOpen);
        }
        catch (InvalidOperationException) { return false; }
    }

    private async Task RequestWizardHighlight(WizardHighlightScope scope, bool visible, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(750));
        var response = await engine.RequestAsync("robot.highlightToolSite", scope.Request(visible), deadline.Token);
        if (visible && (!response.TryGetProperty("highlighted", out var shown) || !shown.GetBoolean()))
            throw new InvalidOperationException("The editor did not acknowledge the tool-site marker.");
    }

    private async Task ShowWizardHighlight(CancellationToken cancellation)
    {
        var live = CurrentCalibrationIdentity();
        var shown = new MarkerState.Tool(new WizardHighlightScope(live.ProfileId, live.DeviceId, live.Generation, live.BackendSessionFingerprint,
            live.SceneId, live.RobotId, Wizard.ModelAssetId, Wizard.ModelRevision, Wizard.Answers.GetValueOrDefault("tool", "").Trim()));
        bool entered = false;
        try
        {
            entered = await wizardHighlightGate.WaitAsync(TimeSpan.FromMilliseconds(800), cancellation);
            if (!entered) throw new InvalidOperationException("Tool marker is busy; read the named point again.");
            if (!WizardHighlightCurrent(shown.Scope)) throw new InvalidOperationException("Fresh matching robot subscription is required for the temporary marker.");
            SetMarker(shown); wizardHighlightAttempt = Stopwatch.GetTimestamp();
            await RequestWizardHighlight(shown.Scope, true, cancellation);
            wizardMessage = "Compare the simulation marker with the midpoint between the real jaw tips. Continue only if they represent the same point.";
        }
        catch (Exception error)
        {
            MoveMarker(shown, MarkerState.None);
            wizardMessage = "Named point coordinates are available, but the temporary marker is unavailable: " + error.Message;
        }
        finally { if (entered) wizardHighlightGate.Release(); }
    }

    // Called after measured scene publication. Marker refresh is visual only and
    // must never throw into telemetry/heartbeats or change motion permission.
    internal async Task MaintainWizardHighlight(CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested) return;
        bool entered = false;
        try
        {
            entered = await wizardHighlightGate.WaitAsync(0, cancellation);
            if (entered) await MaintainWizardMarkerWithinGate(cancellation);
        }
        catch (Exception error)
        {
            if (wizardMarker is MarkerState.Tool tool) MoveMarker(tool, MarkerState.None);
            wizardMessage = "Temporary tool marker paused: " + error.Message;
        }
        finally { if (entered) wizardHighlightGate.Release(); }
    }

    private async Task MaintainWizardMarkerWithinGate(CancellationToken cancellation)
    {
        await MaintainSheetMarks(cancellation);
        switch (wizardMarker)
        {
            case not MarkerState.Tool when WizardReferenceStep:
                await MaintainWizardReferenceWithinGate(cancellation);
                break;
            case MarkerState.Edge edge:
                MoveMarker(edge, MarkerState.None);
                await TryMarkerRequest(RequestWizardReference(edge.Scope, false, cancellation), "The desk pointer will expire: ");
                break;
            case MarkerState.Tool tool when !WizardHighlightCurrent(tool.Scope):
                MoveMarker(tool, MarkerState.None);
                await TryMarkerRequest(RequestWizardHighlight(tool.Scope, false, cancellation), "Tool marker paused after its context changed: ");
                break;
            case MarkerState.Tool tool when Stopwatch.GetElapsedTime(wizardHighlightAttempt).TotalSeconds >= 10:
                wizardHighlightAttempt = Stopwatch.GetTimestamp();
                if (!await TryMarkerRequest(RequestWizardHighlight(tool.Scope, true, cancellation), "Temporary tool marker paused; read the named point again. "))
                    MoveMarker(tool, MarkerState.None);
                break;
        }
    }

    // A failed visual request only reports; the marker expires natively.
    private async Task<bool> TryMarkerRequest(Task request, string failure)
    {
        try { await request; return true; }
        catch (Exception error) { wizardMessage = failure + error.Message; return false; }
    }

    // Clear only the exact previously pinned scene/root/model. In particular,
    // never substitute a newly selected robot while leaving the wizard.
    private async Task ClearWizardHighlight(CancellationToken cancellation)
    {
        var previous = Interlocked.Exchange(ref wizardMarker, MarkerState.None);
        bool clearSheet = sheetMarkersPublished;
        sheetMarkersPublished = false;
        if (previous is MarkerState.Hidden && !clearSheet) return;
        bool entered = false;
        try
        {
            entered = await wizardHighlightGate.WaitAsync(TimeSpan.FromMilliseconds(800), cancellation);
            if (!entered) return; // Native marker expires and clears on unload.
            if (wizardMarker is not MarkerState.Hidden) return; // A newer marker owns this process now.
            try
            {
                if (clearSheet)
                    for (int row = 0; row < 5; row++)
                        await engine.RequestAsync("robot.highlightReference", new JsonObject { ["visible"] = false, ["markerId"] = "desk-point-" + row }, cancellation);
                if (previous is MarkerState.Edge edge) await RequestWizardReference(edge.Scope, false, cancellation);
                else if (previous is MarkerState.Tool tool) await RequestWizardHighlight(tool.Scope, false, cancellation);
            }
            catch (Exception error) { wizardMessage = "Temporary marker clear was not acknowledged; it will expire. " + error.Message; }
        }
        catch (OperationCanceledException) { SetMarker(MarkerState.None); }
        finally { if (entered) wizardHighlightGate.Release(); }
    }
}

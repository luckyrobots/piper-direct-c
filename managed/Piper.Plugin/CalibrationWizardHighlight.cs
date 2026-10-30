using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record WizardHighlightScope(string ProfileId, string DeviceId, long Generation, string BackendFingerprint,
    string SceneId, string RobotId, string ModelAssetId, string ModelRevision, string SiteName)
{
    public bool Matches(CalibrationIdentity live, string site, string asset, string revision, bool calibrationOpen, bool fresh) => calibrationOpen && fresh &&
        live.ProfileId == ProfileId && live.DeviceId == DeviceId && live.Generation == Generation && live.BackendSessionFingerprint == BackendFingerprint &&
        live.SceneId == SceneId && live.RobotId == RobotId && site == SiteName && asset == ModelAssetId && revision == ModelRevision;
    public JsonObject Request(bool visible) => new() { ["sceneId"] = SceneId, ["entityId"] = RobotId,
        ["expectedModelAssetId"] = ModelAssetId, ["expectedModelRevision"] = ModelRevision, ["siteName"] = SiteName,
        ["durationSeconds"] = 30, ["visible"] = visible };
}

public sealed partial class PipersPlugin
{
    private readonly SemaphoreSlim wizardHighlightGate = new(1);
    private WizardHighlightScope? wizardHighlightScope;
    private long wizardHighlightAttempt;

    private bool WizardHighlightCurrent(WizardHighlightScope scope)
    {
        try
        {
            _ = Device(scope.DeviceId, scope.Generation);
            return calibrationWizard is not null && scope.Matches(CurrentCalibrationIdentity(), Wizard.Answers.GetValueOrDefault("tool", "").Trim(),
                Wizard.ModelAssetId, Wizard.ModelRevision, calibrationOpen, true);
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
        var scope = new WizardHighlightScope(live.ProfileId, live.DeviceId, live.Generation, live.BackendSessionFingerprint,
            live.SceneId, live.RobotId, Wizard.ModelAssetId, Wizard.ModelRevision, Wizard.Answers.GetValueOrDefault("tool", "").Trim());
        bool entered = false;
        try
        {
            entered = await wizardHighlightGate.WaitAsync(TimeSpan.FromMilliseconds(800), cancellation);
            if (!entered) throw new InvalidOperationException("Tool marker is busy; read the named point again.");
            if (!WizardHighlightCurrent(scope)) throw new InvalidOperationException("Fresh matching robot subscription is required for the temporary marker.");
            wizardHighlightScope = scope; wizardHighlightAttempt = Stopwatch.GetTimestamp();
            await RequestWizardHighlight(scope, true, cancellation);
            wizardMessage = "Temporary scene marker shows the NAMED MODEL site, not a software-verified physical contact point. Compare it with the real midpoint before confirming.";
        }
        catch (Exception error)
        {
            if (ReferenceEquals(wizardHighlightScope, scope)) wizardHighlightScope = null;
            wizardMessage = "Named point coordinates are available, but the temporary marker is unavailable: " + error.Message;
        }
        finally { if (entered) wizardHighlightGate.Release(); }
    }

    // Called after measured scene publication. Marker refresh is visual only and
    // must never throw into telemetry/heartbeats or change motion permission.
    private async Task MaintainWizardHighlight(CancellationToken cancellation)
    {
        bool entered = false;
        try
        {
            if (wizardHighlightScope is null || cancellation.IsCancellationRequested) return;
            entered = await wizardHighlightGate.WaitAsync(0, cancellation);
            if (!entered) return;
            if (wizardHighlightScope is not { } scope) return;
            if (!WizardHighlightCurrent(scope))
            {
                wizardHighlightScope = null;
                try { await RequestWizardHighlight(scope, false, cancellation); }
                catch (Exception error) { wizardMessage = "Tool marker paused after its context changed: " + error.Message; }
                return;
            }
            if (Stopwatch.GetElapsedTime(wizardHighlightAttempt).TotalSeconds < 10) return;
            wizardHighlightAttempt = Stopwatch.GetTimestamp();
            try { await RequestWizardHighlight(scope, true, cancellation); }
            catch (Exception error) { wizardHighlightScope = null; wizardMessage = "Temporary tool marker paused; read the named point again. " + error.Message; }
        }
        catch (Exception error) { wizardHighlightScope = null; wizardMessage = "Temporary tool marker paused: " + error.Message; }
        finally { if (entered) wizardHighlightGate.Release(); }
    }

    // Clear only the exact previously pinned scene/root/model. In particular,
    // never substitute a newly selected robot while leaving the wizard.
    private async Task ClearWizardHighlight(CancellationToken cancellation)
    {
        var scope = Interlocked.Exchange(ref wizardHighlightScope, null);
        if (scope is null) return;
        bool entered = false;
        try
        {
            entered = await wizardHighlightGate.WaitAsync(TimeSpan.FromMilliseconds(800), cancellation);
            if (!entered) return; // Native marker expires and clears on unload.
            if (wizardHighlightScope is not null) return; // A newer marker owns this process now.
            try { await RequestWizardHighlight(scope, false, cancellation); }
            catch (Exception error) { wizardMessage = "Temporary marker clear was not acknowledged; it will expire. " + error.Message; }
        }
        catch (OperationCanceledException) { wizardHighlightScope = null; }
        finally { if (entered) wizardHighlightGate.Release(); }
    }
}

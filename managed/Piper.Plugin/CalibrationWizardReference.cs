using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

/// <summary>A temporary scene pointer, never a physical or simulated motion target.</summary>
public sealed record WizardReferenceHighlightScope(CalibrationIdentity Identity, long Epoch, CalibrationWizardStep Step,
    double WidthMm, double DepthMm, double SurfaceMm, CalibrationPoint Point, string Label)
{
    public static WizardReferenceHighlightScope Create(CalibrationIdentity identity, CalibrationWizardDraft draft)
    {
        if (draft.Step is not (CalibrationWizardStep.DeskFrame or CalibrationWizardStep.DeskBack) ||
            !draft.Answers.ContainsKey("sceneconfirmed") || string.IsNullOrWhiteSpace(draft.ModelAssetId) || string.IsNullOrWhiteSpace(draft.ModelRevision) || identity.ProfileId != draft.ProfileId ||
            identity.ModelAssetId != draft.ModelAssetId || identity.ModelRevision != draft.ModelRevision)
            throw new InvalidOperationException("First confirm the simulated tabletop and the jaw-tip marker on this robot.");
        double width = CalibrationWizardFlow.Number(draft, "width", 100, 10000);
        double depth = CalibrationWizardFlow.Number(draft, "depth", 100, 10000);
        double surface = CalibrationWizardFlow.SceneSurface(draft);
        bool left = draft.Step == CalibrationWizardStep.DeskFrame;
        return new(identity, draft.Epoch, draft.Step, width, depth, surface,
            left ? new(-width / 2000, surface / 1000 + .003, 0) : new(0, surface / 1000 + .003, -depth / 2000),
            left ? "LEFT desk edge" : "BACK desk edge");
    }
    public bool Matches(CalibrationIdentity live, CalibrationWizardDraft draft, bool calibrationOpen, bool fresh)
    {
        if (!calibrationOpen || !fresh) return false;
        try { return this == Create(live, draft); }
        catch (InvalidOperationException) { return false; }
    }
    public string DeskFingerprint() => DeskFingerprint(Identity, WidthMm, DepthMm, SurfaceMm);
    public static string DeskFingerprint(CalibrationIdentity identity, double widthMm, double depthMm, double surfaceMm) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Identity = identity, WidthMm = widthMm, DepthMm = depthMm, SurfaceMm = surfaceMm }, PluginProtocol.Json))));
    public static string FailureMessage(Exception error)
    {
        string message = error.Message;
        bool missingApi = error is NotImplementedException || new[] { "Unsupported robot API", "Unsupported or unauthorized editor API: robot.highlightReference",
            "unknown request", "unknown method", "method not found", "unrecognized request", "unsupported request method" }
            .Any(text => message.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (missingApi) return "Update Lucky Engine to show desk markers, then retry Show edge. Confirmation remains unavailable until the pointer is shown.";
        if (error is OperationCanceledException) return "The editor did not answer in time. Use Show edge to retry. Confirmation remains unavailable.";
        return "Desk-edge pointer unavailable. Use Show edge to retry; confirmation remains unavailable. " + message;
    }
    public JsonObject Request(bool visible) => new() { ["sceneId"] = Identity.SceneId, ["entityId"] = Identity.RobotId,
        ["expectedModelAssetId"] = Identity.ModelAssetId, ["expectedModelRevision"] = Identity.ModelRevision,
        ["point"] = Point.Json(), ["label"] = Label, ["durationSeconds"] = 30, ["visible"] = visible };
    public bool Acknowledged(JsonElement response)
    {
        if (!response.TryGetProperty("highlighted", out var shown) || shown.ValueKind != JsonValueKind.True ||
            !response.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String || label.GetString() != Label ||
            !response.TryGetProperty("point", out var point) || point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 3) return false;
        var values = point.EnumerateArray().ToArray();
        return values.All(v => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double n) && double.IsFinite(n)) &&
            Point.Distance(new(values[0].GetDouble(), values[1].GetDouble(), values[2].GetDouble())) <= 1e-9;
    }
}

public sealed partial class PipersPlugin
{
    private WizardReferenceHighlightScope? wizardReferenceHighlightScope;
    private long wizardReferenceAcknowledgedAt;
    private bool wizardReferenceRetryRequired;
    private bool WizardReferenceStep => calibrationWizard?.Step is CalibrationWizardStep.DeskFrame or CalibrationWizardStep.DeskBack;
    private CalibrationIdentity WizardReferenceIdentity() => CurrentCalibrationIdentity() with
    { ModelAssetId = Wizard.ModelAssetId, ModelRevision = Wizard.ModelRevision };

    private bool WizardReferenceCurrent(WizardReferenceHighlightScope scope)
    {
        try
        {
            _ = Device(scope.Identity.DeviceId, scope.Identity.Generation);
            return calibrationWizard is not null && scope.Matches(WizardReferenceIdentity(), Wizard, calibrationOpen, true);
        }
        catch (InvalidOperationException) { return false; }
    }
    private bool WizardReferenceShown() => wizardReferenceHighlightScope is { } scope && wizardReferenceAcknowledgedAt != 0 &&
        Stopwatch.GetElapsedTime(wizardReferenceAcknowledgedAt).TotalSeconds < 20 && WizardReferenceCurrent(scope);

    private async Task RequestWizardReference(WizardReferenceHighlightScope scope, bool visible, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(750));
        var response = await engine.RequestAsync("robot.highlightReference", scope.Request(visible), deadline.Token);
        if (visible && !scope.Acknowledged(response)) throw new InvalidOperationException("The editor did not confirm the requested desk-edge pointer.");
    }

    private async Task ShowWizardReferenceWithinGate(CancellationToken cancellation)
    {
        wizardReferenceAcknowledgedAt = 0;
        var scope = WizardReferenceHighlightScope.Create(WizardReferenceIdentity(), Wizard);
        if (!WizardReferenceCurrent(scope)) throw new InvalidOperationException("Wait for the linked robot to reconnect before showing the desk edge.");
        wizardHighlightScope = null; // The native process owns one visual marker.
        wizardReferenceHighlightScope = scope; wizardHighlightAttempt = Stopwatch.GetTimestamp();
        await RequestWizardReference(scope, true, cancellation);
        if (!WizardReferenceCurrent(scope)) throw new InvalidOperationException("The robot or question changed while the pointer was being shown. Show it again.");
        wizardReferenceAcknowledgedAt = Stopwatch.GetTimestamp();
        wizardMessage = "The simulation pointer marks the " + (scope.Step == CalibrationWizardStep.DeskFrame ? "LEFT" : "BACK") + " desk edge. Compare it with the edge of your real desk before confirming.";
    }

    private async Task ShowWizardReference(CancellationToken cancellation)
    {
        bool entered = false;
        try
        {
            wizardReferenceRetryRequired = false;
            entered = await wizardHighlightGate.WaitAsync(TimeSpan.FromMilliseconds(800), cancellation);
            if (!entered) throw new InvalidOperationException("The pointer is busy. Try Show edge again.");
            await ShowWizardReferenceWithinGate(cancellation);
        }
        catch (Exception error)
        {
            wizardReferenceAcknowledgedAt = 0; wizardReferenceRetryRequired = true;
            wizardMessage = WizardReferenceHighlightScope.FailureMessage(error);
        }
        finally { if (entered) wizardHighlightGate.Release(); }
    }

    // The caller owns wizardHighlightGate. This is invoked by the existing
    // nonblocking visual-maintenance task, never by robot command/heartbeat code.
    private async Task MaintainWizardReferenceWithinGate(CancellationToken cancellation)
    {
        if (wizardReferenceRetryRequired) return; // Failed/unsupported markers require the visible Retry action.
        bool current = wizardReferenceHighlightScope is { } scope && WizardReferenceCurrent(scope);
        if (!current) wizardReferenceAcknowledgedAt = 0;
        if (Stopwatch.GetElapsedTime(wizardHighlightAttempt).TotalSeconds < 10) return;
        wizardHighlightAttempt = Stopwatch.GetTimestamp();
        try { await ShowWizardReferenceWithinGate(cancellation); }
        catch (Exception error) { wizardReferenceAcknowledgedAt = 0; wizardReferenceRetryRequired = true; wizardMessage = WizardReferenceHighlightScope.FailureMessage(error); }
    }

    private void ConfirmWizardReference()
    {
        if (!WizardReferenceShown() || wizardReferenceHighlightScope is not { } scope)
            throw new InvalidOperationException(wizardReferenceRetryRequired && wizardMessage.Length > 0 ? wizardMessage :
                "First show the pointer in the simulation, then compare it with the matching real desk edge.");
        if (Wizard.Step == CalibrationWizardStep.DeskFrame) Wizard.Answers["leftedgeconfirmed"] = scope.DeskFingerprint();
        else
        {
            if (Wizard.Answers.GetValueOrDefault("leftedgeconfirmed") != scope.DeskFingerprint())
                throw new InvalidOperationException("The desk or robot changed since LEFT was confirmed. Go back and confirm the LEFT edge again.");
            Wizard.Answers["frame"] = scope.DeskFingerprint();
        }
    }
}

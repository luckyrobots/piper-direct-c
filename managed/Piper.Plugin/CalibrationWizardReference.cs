using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

/// <summary>A temporary scene pointer, never a physical or simulated motion target.</summary>
public sealed record WizardReferenceHighlightScope(CalibrationIdentity Identity, long Epoch, CalibrationWizardStep Step,
    double WidthMm, double DepthMm, double SurfaceMm, CalibrationPoint Point, string Label, CalibrationPlacement RootPlacement)
{
    public static WizardReferenceHighlightScope Create(CalibrationIdentity identity, CalibrationWizardDraft draft, CalibrationPlacement rootPlacement)
    {
        if (draft.Step is not (CalibrationWizardStep.DeskFrame or CalibrationWizardStep.DeskBack) ||
            !draft.Answers.ContainsKey("sceneconfirmed") || string.IsNullOrWhiteSpace(draft.ModelAssetId) || string.IsNullOrWhiteSpace(draft.ModelRevision) || identity.ProfileId != draft.ProfileId ||
            identity.ModelAssetId != draft.ModelAssetId || identity.ModelRevision != draft.ModelRevision)
            throw new InvalidOperationException("First confirm the simulated tabletop and the jaw-tip marker on this robot.");
        double width = CalibrationWizardFlow.Number(draft, "width", 100, 10000);
        double depth = CalibrationWizardFlow.Number(draft, "depth", 100, 10000);
        double surface = CalibrationWizardFlow.SceneSurface(draft);
        _ = CalibrationPlacement.Read(rootPlacement.Json());
        if (Math.Abs(rootPlacement.Position.Y - surface / 1000) > .001)
            throw new InvalidOperationException("The simulated robot base is no longer on the confirmed tabletop. Check its placement and start new questions.");
        bool left = draft.Step == CalibrationWizardStep.DeskFrame;
        return new(identity, draft.Epoch, draft.Step, width, depth, surface,
            left ? new(-width / 2000, surface / 1000 + .003, 0) : new(0, surface / 1000 + .003, -depth / 2000),
            left ? "LEFT desk edge" : "BACK desk edge", rootPlacement);
    }
    public bool Matches(CalibrationIdentity live, CalibrationWizardDraft draft, CalibrationPlacement rootPlacement, bool calibrationOpen, bool fresh)
    {
        if (!calibrationOpen || !fresh) return false;
        try
        {
            var expected = Create(live, draft, rootPlacement);
            return Identity == expected.Identity && Epoch == expected.Epoch && Step == expected.Step && WidthMm == expected.WidthMm && DepthMm == expected.DepthMm &&
                SurfaceMm == expected.SurfaceMm && Point == expected.Point && Label == expected.Label && RootPlacement.Same(expected.RootPlacement);
        }
        catch (InvalidOperationException) { return false; }
    }
    public string DeskFingerprint() => DeskFingerprint(Identity, WidthMm, DepthMm, SurfaceMm, RootPlacement);
    public static string DeskFingerprint(CalibrationIdentity identity, double widthMm, double depthMm, double surfaceMm, CalibrationPlacement rootPlacement) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Identity = identity, WidthMm = widthMm, DepthMm = depthMm, SurfaceMm = surfaceMm, RootPlacement = rootPlacement.Json() }, PluginProtocol.Json))));
    public static bool RefreshDue(WizardReferenceHighlightScope? marker, CalibrationWizardDraft question, TimeSpan sinceAttempt) =>
        marker is null || marker.Epoch != question.Epoch || marker.Step != question.Step || sinceAttempt >= TimeSpan.FromSeconds(10);
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
    public JsonObject Request(bool visible, bool frame = false) => new() { ["sceneId"] = Identity.SceneId, ["entityId"] = Identity.RobotId,
        ["expectedModelAssetId"] = Identity.ModelAssetId, ["expectedModelRevision"] = Identity.ModelRevision,
        ["expectedRootPlacement"] = RootPlacement.Json(), ["frame"] = frame,
        ["point"] = Point.Json(), ["label"] = Label, ["durationSeconds"] = 30, ["visible"] = visible };
    public bool Acknowledged(JsonElement response, bool frameRequested = false)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("highlighted", out var shown) || shown.ValueKind != JsonValueKind.True ||
            !response.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String || label.GetString() != Label ||
            !response.TryGetProperty("point", out var point) || point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 3 ||
            !response.TryGetProperty("framed", out var framed) || framed.ValueKind != (frameRequested ? JsonValueKind.True : JsonValueKind.False) ||
            !response.TryGetProperty("rootPlacement", out var root)) return false;
        var values = point.EnumerateArray().ToArray();
        try
        {
            return values.All(v => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double n) && double.IsFinite(n)) &&
                Point.Distance(new(values[0].GetDouble(), values[1].GetDouble(), values[2].GetDouble())) <= 1e-9 &&
                RootPlacement.Same(CalibrationPlacement.Read(JsonNode.Parse(root.GetRawText())));
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException or ArgumentException) { return false; }
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
            return calibrationWizard is not null && scope.Matches(WizardReferenceIdentity(), Wizard, scope.RootPlacement, calibrationOpen, true);
        }
        catch (InvalidOperationException) { return false; }
    }
    private bool WizardReferenceShown() => wizardReferenceHighlightScope is { } scope && wizardReferenceAcknowledgedAt != 0 &&
        Stopwatch.GetElapsedTime(wizardReferenceAcknowledgedAt).TotalSeconds < 20 && WizardReferenceCurrent(scope);

    private async Task RequestWizardReference(WizardReferenceHighlightScope scope, bool visible, CancellationToken cancellation, bool frame = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(750));
        var response = await engine.RequestAsync("robot.highlightReference", scope.Request(visible, frame), deadline.Token);
        if (visible && !scope.Acknowledged(response, frame)) throw new InvalidOperationException("The editor did not confirm the requested desk-edge pointer, framing, and robot placement. Update Lucky Engine if these marker features are unavailable.");
    }

    private async Task ShowWizardReferenceWithinGate(CancellationToken cancellation, bool frame)
    {
        wizardReferenceAcknowledgedAt = 0;
        var identity = WizardReferenceIdentity();
        long epoch = Wizard.Epoch, start = Stopwatch.GetTimestamp();
        _ = Device(identity.DeviceId, identity.Generation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(750));
        var currentRoot = await ReadWizardMetricRoot(deadline.Token);
        if (!calibrationOpen || epoch != Wizard.Epoch || identity != WizardReferenceIdentity() || Stopwatch.GetElapsedTime(start).TotalSeconds > .5)
            throw new InvalidOperationException("The robot or question changed, or the simulation answered too slowly. Use Show edge again.");
        _ = Device(identity.DeviceId, identity.Generation);
        CalibrationPlacement pinnedRoot = currentRoot;
        if (Wizard.Answers.TryGetValue("edgeroot", out string? savedRoot))
            pinnedRoot = CalibrationPlacement.Read(JsonNode.Parse(savedRoot));
        else if (wizardReferenceHighlightScope is { } previous && previous.Epoch == epoch && previous.Step == Wizard.Step)
            pinnedRoot = previous.RootPlacement;
        else if (Wizard.Step == CalibrationWizardStep.DeskBack)
            throw new InvalidOperationException("Go back and confirm the LEFT edge first. Both edges must use the same simulated robot placement.");
        if (!pinnedRoot.Same(currentRoot))
            throw new InvalidOperationException("The simulated robot was moved after these desk questions began. Start new questions and compare both edges again; the pointer will not follow a changed placement silently.");
        var scope = WizardReferenceHighlightScope.Create(identity, Wizard, pinnedRoot);
        if (!WizardReferenceCurrent(scope)) throw new InvalidOperationException("Wait for the linked robot to reconnect before showing the desk edge.");
        wizardHighlightScope = null; // The native process owns one visual marker.
        wizardReferenceHighlightScope = scope; wizardHighlightAttempt = Stopwatch.GetTimestamp();
        await RequestWizardReference(scope, true, cancellation, frame);
        if (!WizardReferenceCurrent(scope)) throw new InvalidOperationException("The robot or question changed while the pointer was being shown. Show it again.");
        wizardReferenceAcknowledgedAt = Stopwatch.GetTimestamp();
        wizardMessage = "The simulation pointer marks the " + (scope.Step == CalibrationWizardStep.DeskFrame ? "LEFT" : "BACK") + " desk edge. Compare it with the edge of your real desk before confirming.";
    }

    private async Task ShowWizardReference(CancellationToken cancellation, bool frame = true)
    {
        bool entered = false;
        try
        {
            wizardReferenceRetryRequired = false;
            entered = await wizardHighlightGate.WaitAsync(TimeSpan.FromMilliseconds(800), cancellation);
            if (!entered) throw new InvalidOperationException("The pointer is busy. Try Show edge again.");
            await ShowWizardReferenceWithinGate(cancellation, frame);
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
        bool firstQuestionMarker = wizardReferenceHighlightScope is not { } existing || existing.Epoch != Wizard.Epoch || existing.Step != Wizard.Step;
        if (!WizardReferenceHighlightScope.RefreshDue(wizardReferenceHighlightScope, Wizard, Stopwatch.GetElapsedTime(wizardHighlightAttempt))) return;
        wizardHighlightAttempt = Stopwatch.GetTimestamp();
        try { await ShowWizardReferenceWithinGate(cancellation, firstQuestionMarker); }
        catch (Exception error) { wizardReferenceAcknowledgedAt = 0; wizardReferenceRetryRequired = true; wizardMessage = WizardReferenceHighlightScope.FailureMessage(error); }
    }

    private void ConfirmWizardReference()
    {
        if (!WizardReferenceShown() || wizardReferenceHighlightScope is not { } scope)
            throw new InvalidOperationException(wizardReferenceRetryRequired && wizardMessage.Length > 0 ? wizardMessage :
                "First show the pointer in the simulation, then compare it with the matching real desk edge.");
        if (Wizard.Step == CalibrationWizardStep.DeskFrame)
        {
            Wizard.Answers["edgeroot"] = scope.RootPlacement.Json().ToJsonString();
            Wizard.Answers["leftedgeconfirmed"] = scope.DeskFingerprint();
        }
        else
        {
            if (Wizard.Answers.GetValueOrDefault("leftedgeconfirmed") != scope.DeskFingerprint())
                throw new InvalidOperationException("The desk or robot changed since LEFT was confirmed. Go back and confirm the LEFT edge again.");
            Wizard.Answers["frame"] = scope.DeskFingerprint();
        }
    }
}

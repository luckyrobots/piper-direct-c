using System.Globalization;
using System.Text.Json.Serialization;

namespace LuckyRobots.Piper.Plugin;

// Saved drafts store these numeric values, so the numbers are fixed. Values
// written by the removed question-by-question wizard are mapped by Legacy().
public enum CalibrationWizardStep
{
    Prepare = 0, ModelPoint = 2, DeskFrame = 3, PointIntro = 14, Capture = 21, Review = 22, InspectSaved = 24, DeskBack = 25
}

/// <summary>The in-memory draft. Answers holds typed-in measurements; Setup holds the confirmations.</summary>
public sealed record CalibrationWizardDraft(int Schema, string ProfileId, CalibrationWizardStep Step, long Epoch,
    Dictionary<string, string> Answers, string ReportFile = "", string HelpCamera = "bench",
    string ModelAssetId = "", string ModelRevision = "")
{
    [JsonIgnore] public SheetSetup Setup { get; init; } = new();
}

/// <summary>
/// The saved draft file. Setup confirmations are stored as the Answers keys
/// and the OperatorSaysTcpMatches flag that earlier versions wrote, so old and
/// new files read the same way.
/// </summary>
public sealed record CalibrationWizardFile(int Schema, string ProfileId, CalibrationWizardStep Step, long Epoch,
    Dictionary<string, string> Answers, string ReportFile = "", string HelpCamera = "bench",
    string ModelAssetId = "", string ModelRevision = "", bool OperatorSaysTcpMatches = false)
{
    private const string EdgeRootKey = "edgeroot", LeftKey = "leftedgeconfirmed", BackKey = "frame",
        JawTipsKey = "physicalpoint", SurfaceKey = "sceneconfirmed", PreviewKey = "sheetreferencepreview";

    public static CalibrationWizardFile From(CalibrationWizardDraft draft)
    {
        var answers = new Dictionary<string, string>(draft.Answers, StringComparer.Ordinal);
        var setup = draft.Setup;
        void Put(string key, string value) { if (value.Length > 0) answers[key] = value; }
        Put(EdgeRootKey, setup.EdgeRoot); Put(LeftKey, setup.LeftProof); Put(BackKey, setup.BackProof);
        Put(JawTipsKey, setup.JawTipsConfirmed ? "operator confirmed midpoint between both jaw tips" : "");
        Put(SurfaceKey, setup.SurfaceConfirmed ? "operator confirmed simulated base rests on tabletop" : "");
        Put(PreviewKey, setup.ReferencePreviewed ? "read-only visual reference; not operator confirmation" : "");
        return new(draft.Schema, draft.ProfileId, draft.Step, draft.Epoch, answers, draft.ReportFile, draft.HelpCamera,
            draft.ModelAssetId, draft.ModelRevision, setup.JawTipsConfirmed);
    }

    public CalibrationWizardDraft ToDraft()
    {
        if (Answers is null) throw new InvalidOperationException("Saved wizard progress is invalid. Keep its report and start a new draft.");
        var answers = new Dictionary<string, string>(Answers, StringComparer.Ordinal);
        string Take(string key) => answers.Remove(key, out string? value) ? value ?? "" : "";
        // Older versions cleared only the flag in two places; both must agree.
        var setup = SheetSetup.Restore(Take(EdgeRootKey), Take(LeftKey), Take(BackKey),
            Take(JawTipsKey).Length > 0 && OperatorSaysTcpMatches, Take(SurfaceKey).Length > 0, Take(PreviewKey).Length > 0);
        return new CalibrationWizardDraft(Schema, ProfileId, CalibrationWizardFlow.Legacy(Step), Epoch, answers, ReportFile, HelpCamera,
            ModelAssetId, ModelRevision) { Setup = setup };
    }
}

public sealed record CalibrationTabletopInputs(CalibrationDesk Desk, CalibrationMeasurement Measurement);

/// <summary>Calibration draft compatibility and measurement validation.</summary>
public static class CalibrationWizardFlow
{
    public static CalibrationWizardDraft New(string profileId, long epoch = 1) => new(2, profileId, CalibrationWizardStep.Prepare, epoch,
        new(StringComparer.Ordinal) { ["tool"] = "grasp_site", ["role"] = "training", ["method"] = "edge measurements" });
    public static double Number(CalibrationWizardDraft draft, string key, double minimum, double maximum)
    {
        if (!draft.Answers.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text) ||
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) ||
            value < minimum || value > maximum)
            throw new InvalidOperationException($"Enter a measured {key} value from {minimum:0.###} to {maximum:0.###} mm. No value is assumed.");
        return value;
    }
    public static double SceneSurface(CalibrationWizardDraft draft, double? inspectedRootYMillimetres = null)
    {
        double sceneY = Number(draft, "sceney", 0, 3000);
        if (inspectedRootYMillimetres.HasValue && (!double.IsFinite(inspectedRootYMillimetres.Value) || Math.Abs(sceneY - inspectedRootYMillimetres.Value) > 1))
            throw new InvalidOperationException("Scene tabletop Y and the inspected base root differ. Edit/verify the scene desk/root setup; the wizard will not float the base to hide a mismatch.");
        return sceneY;
    }
    public static CalibrationWizardDraft NextPoint(CalibrationWizardDraft draft, int trainingCount, int validationCount)
    {
        var answers = new Dictionary<string, string>(draft.Answers, StringComparer.Ordinal);
        foreach (string field in new[] { "left", "back", "height", "column", "row", "cross" }) answers.Remove(field);
        answers["role"] = trainingCount < 4 ? "training" : "validation";
        return draft with { Answers = answers, Step = validationCount > 0 ? CalibrationWizardStep.Review : CalibrationWizardStep.PointIntro,
            Epoch = draft.Epoch + 1 };
    }
    public static bool Accepts(CalibrationWizardDraft draft, string profileId, long epoch) => draft.ProfileId == profileId && draft.Epoch == epoch;
    // Maps a step value written by the removed question-by-question wizard:
    // earlier questions restart at Prepare, later ones at the point list.
    public static CalibrationWizardStep Legacy(CalibrationWizardStep step) =>
        Enum.IsDefined(step) ? step : (int)step < (int)CalibrationWizardStep.PointIntro ? CalibrationWizardStep.Prepare : CalibrationWizardStep.PointIntro;
    public static CalibrationWizardDraft TabletopDefault(CalibrationWizardDraft draft)
    {
        // Uncaptured older drafts used one abstract desk-frame confirmation before
        // dimensions. Ask the new pointer questions; never manufacture an ack.
        if (draft.Schema == 1 && draft.ReportFile.Length == 0 && draft.Step is not (CalibrationWizardStep.Prepare or CalibrationWizardStep.ModelPoint))
            return draft with { Schema = 2, Setup = draft.Setup.ClearEdges(), Step = CalibrationWizardStep.Prepare, Epoch = draft.Epoch + 1 };
        return draft with { Schema = 2, Epoch = draft.Epoch + 1 };
    }
    public static CalibrationTabletopInputs TabletopInputs(CalibrationWizardDraft draft)
    {
        double width = Number(draft, "width", 100, 10000), depth = Number(draft, "depth", 100, 10000);
        var desk = new CalibrationDesk(width / 1000, depth / 1000, SceneSurface(draft) / 1000, Number(draft, "precision", 1, 10), draft.Answers.GetValueOrDefault("tool", "").Trim());
        desk.Validate();
        var measurement = new CalibrationMeasurement("edge measurements", Number(draft, "left", 0, width), Number(draft, "back", 0, depth), Number(draft, "height", 0, 1000));
        _ = measurement.World(desk);
        return new(desk, measurement);
    }
    public static bool AlreadyRecorded(CalibrationWizardDraft draft, CalibrationSample sample)
    {
        if (draft.Step != CalibrationWizardStep.Capture || sample.HeldOut != (draft.Answers.GetValueOrDefault("role") == "validation") ||
            sample.Measurement.Mode != "edge measurements" || draft.Answers.GetValueOrDefault("method") != "edge measurements") return false;
        try
        {
            bool Equal(string key, double value) => Math.Abs(Number(draft, key, 0, 10000) - value) < 1e-9;
            return Equal("height", sample.Measurement.HeightMm) && Equal("left", sample.Measurement.LeftMm) && Equal("back", sample.Measurement.BackMm);
        }
        catch (InvalidOperationException) { return false; }
    }
    public static void ValidateDraft(CalibrationWizardDraft draft, string profileId)
    {
        if (draft.Schema is not (1 or 2) || draft.ProfileId != profileId || !Enum.IsDefined(draft.Step) || draft.Epoch < 1 || draft.Epoch >= long.MaxValue - 100 || draft.Answers is null ||
            draft.Answers.Count > 64 || draft.Answers.Any(p => p.Key.Length > 32 || p.Value is null || p.Value.Length > 512) ||
            draft.HelpCamera is not ("bench" or "gripper" or "depth") || Path.GetFileName(draft.ReportFile) != draft.ReportFile ||
            draft.ReportFile.Length > 160 || draft.ReportFile.Length > 0 && !draft.ReportFile.EndsWith("-calibration.json", StringComparison.Ordinal))
            throw new InvalidOperationException("Saved wizard progress is invalid. Keep its report and start a new draft.");
    }
}

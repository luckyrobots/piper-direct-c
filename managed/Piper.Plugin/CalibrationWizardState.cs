using System.Globalization;

namespace LuckyRobots.Piper.Plugin;

public enum CalibrationWizardStep
{
    Prepare, ContactPoint, ModelPoint, DeskFrame, SceneSurfaceY, SceneFloorOrigin, DeskWidth, DeskDepth, TabletopHeight, Precision, Method,
    GridSquare, GridOriginLeft, GridOriginBack, PointIntro, PointLeft, PointBack, PointHeight, GridColumn, GridRow,
    HandGuide, Capture, Review, Complete, InspectSaved
}

public sealed record CalibrationWizardDraft(int Schema, string ProfileId, CalibrationWizardStep Step, long Epoch,
    Dictionary<string, string> Answers, CalibrationWizardStep[] History, string ReportFile = "", string HelpCamera = "bench",
    string ModelAssetId = "", string ModelRevision = "", bool OperatorSaysTcpMatches = false);

/// <summary>Question order and input validation only: no engine, camera or robot command path.</summary>
public static class CalibrationWizardFlow
{
    public static CalibrationWizardDraft New(string profileId, long epoch = 1) => new(1, profileId, CalibrationWizardStep.Prepare, epoch,
        new(StringComparer.Ordinal) { ["tool"] = "grasp_site", ["role"] = "training", ["method"] = "edge measurements" }, []);
    public static string? Input(CalibrationWizardStep step) => step switch
    {
        CalibrationWizardStep.ModelPoint => "tool", CalibrationWizardStep.SceneSurfaceY => "sceney", CalibrationWizardStep.SceneFloorOrigin => "floory",
        CalibrationWizardStep.DeskWidth => "width", CalibrationWizardStep.DeskDepth => "depth",
        CalibrationWizardStep.TabletopHeight => "tableheight", CalibrationWizardStep.Precision => "precision", CalibrationWizardStep.GridSquare => "square",
        CalibrationWizardStep.GridOriginLeft => "originleft", CalibrationWizardStep.GridOriginBack => "originback", CalibrationWizardStep.PointLeft => "left",
        CalibrationWizardStep.PointBack => "back", CalibrationWizardStep.PointHeight => "height", CalibrationWizardStep.GridColumn => "column", CalibrationWizardStep.GridRow => "row", _ => null
    };
    public static double Number(CalibrationWizardDraft draft, string key, double minimum, double maximum, bool whole = false)
    {
        if (!draft.Answers.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text) ||
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) ||
            value < minimum || value > maximum || whole && value != Math.Truncate(value))
            throw new InvalidOperationException($"Enter a measured {key} value from {minimum:0.###} to {maximum:0.###}" + (whole ? " as a whole number." : " mm. No value is assumed."));
        return value;
    }
    public static double SceneSurface(CalibrationWizardDraft draft, double? inspectedRootYMillimetres = null)
    {
        double sceneY = Number(draft, "sceney", 0, 3000);
        if (inspectedRootYMillimetres.HasValue && (!double.IsFinite(inspectedRootYMillimetres.Value) || Math.Abs(sceneY - inspectedRootYMillimetres.Value) > 1))
            throw new InvalidOperationException("Scene tabletop Y and the inspected base root differ. Edit/verify the scene desk/root setup; the wizard will not float the base to hide a mismatch.");
        return sceneY;
    }
    public static double RelativeTabletopHeight(CalibrationWizardDraft draft) => SceneSurface(draft) - Number(draft, "floory", -10000, 10000);
    public static void ValidateTabletopHeight(CalibrationWizardDraft draft, double precisionMillimetres)
    {
        if (!double.IsFinite(precisionMillimetres) || precisionMillimetres is < 1 or > 10 ||
            Math.Abs(Number(draft, "tableheight", 1, 3000) - RelativeTabletopHeight(draft)) > precisionMillimetres)
            throw new InvalidOperationException("Physical tabletop height and scene tabletop-minus-floor differ beyond the stated precision. Edit/verify scene desk/root and floor-reference setup; no scene transform was changed.");
    }
    public static CalibrationWizardDraft Advance(CalibrationWizardDraft draft)
    {
        bool grid = draft.Answers.GetValueOrDefault("method") == "grid intersections";
        var next = draft.Step switch
        {
            CalibrationWizardStep.Prepare => CalibrationWizardStep.ContactPoint,
            CalibrationWizardStep.ContactPoint => CalibrationWizardStep.ModelPoint,
            CalibrationWizardStep.ModelPoint => CalibrationWizardStep.DeskFrame,
            CalibrationWizardStep.DeskFrame => CalibrationWizardStep.SceneSurfaceY,
            CalibrationWizardStep.SceneSurfaceY => CalibrationWizardStep.SceneFloorOrigin,
            CalibrationWizardStep.SceneFloorOrigin => CalibrationWizardStep.DeskWidth,
            CalibrationWizardStep.DeskWidth => CalibrationWizardStep.DeskDepth,
            CalibrationWizardStep.DeskDepth => CalibrationWizardStep.TabletopHeight,
            CalibrationWizardStep.TabletopHeight => CalibrationWizardStep.Precision,
            CalibrationWizardStep.Precision => CalibrationWizardStep.Method,
            CalibrationWizardStep.Method => grid ? CalibrationWizardStep.GridSquare : CalibrationWizardStep.PointIntro,
            CalibrationWizardStep.GridSquare => CalibrationWizardStep.GridOriginLeft,
            CalibrationWizardStep.GridOriginLeft => CalibrationWizardStep.GridOriginBack,
            CalibrationWizardStep.GridOriginBack => CalibrationWizardStep.PointIntro,
            CalibrationWizardStep.PointIntro => grid ? CalibrationWizardStep.GridColumn : CalibrationWizardStep.PointLeft,
            CalibrationWizardStep.PointLeft => CalibrationWizardStep.PointBack,
            CalibrationWizardStep.PointBack => CalibrationWizardStep.PointHeight,
            CalibrationWizardStep.GridColumn => CalibrationWizardStep.GridRow,
            CalibrationWizardStep.GridRow => CalibrationWizardStep.PointHeight,
            CalibrationWizardStep.PointHeight => CalibrationWizardStep.HandGuide,
            CalibrationWizardStep.HandGuide => CalibrationWizardStep.Capture,
            _ => throw new InvalidOperationException("This step requires its explicit capture, review or scene-only action.")
        };
        return draft with { Step = next, Epoch = draft.Epoch + 1, History = draft.History.Append(draft.Step).ToArray() };
    }
    public static CalibrationWizardDraft Back(CalibrationWizardDraft draft) => draft.History.Length == 0 ? draft :
        draft with { Step = draft.History[^1], Epoch = draft.Epoch + 1, History = draft.History[..^1] };
    public static CalibrationWizardDraft NextPoint(CalibrationWizardDraft draft, int trainingCount, int validationCount)
    {
        var answers = new Dictionary<string, string>(draft.Answers, StringComparer.Ordinal);
        foreach (string field in new[] { "left", "back", "height", "column", "row", "cross" }) answers.Remove(field);
        answers["role"] = trainingCount < 4 ? "training" : "validation";
        return draft with { Answers = answers, Step = validationCount > 0 ? CalibrationWizardStep.Review : CalibrationWizardStep.PointIntro,
            History = [], Epoch = draft.Epoch + 1 };
    }
    public static bool Accepts(CalibrationWizardDraft draft, string profileId, long epoch) => draft.ProfileId == profileId && draft.Epoch == epoch;
    public static bool AlreadyRecorded(CalibrationWizardDraft draft, CalibrationSample sample)
    {
        if (draft.Step != CalibrationWizardStep.Capture || sample.HeldOut != (draft.Answers.GetValueOrDefault("role") == "validation") ||
            sample.Measurement.Mode != draft.Answers.GetValueOrDefault("method")) return false;
        try
        {
            bool Equal(string key, double value) => Math.Abs(Number(draft, key, 0, 10000) - value) < 1e-9;
            if (!Equal("height", sample.Measurement.HeightMm)) return false;
            return sample.Measurement.Mode == "grid intersections"
                ? Equal("square", sample.Measurement.SquareMm) && Equal("originleft", sample.Measurement.OriginLeftMm) && Equal("originback", sample.Measurement.OriginBackMm) && Equal("column", sample.Measurement.Column) && Equal("row", sample.Measurement.Row)
                : Equal("left", sample.Measurement.LeftMm) && Equal("back", sample.Measurement.BackMm);
        }
        catch (InvalidOperationException) { return false; }
    }
    public static void ValidateDraft(CalibrationWizardDraft draft, string profileId)
    {
        if (draft.Schema != 1 || draft.ProfileId != profileId || !Enum.IsDefined(draft.Step) || draft.Epoch < 1 || draft.Epoch >= long.MaxValue - 100 || draft.Answers is null ||
            draft.Answers.Count > 32 || draft.Answers.Any(p => p.Key.Length > 32 || p.Value is null || p.Value.Length > 512) ||
            draft.History is null || draft.History.Length > 24 || draft.History.Any(s => !Enum.IsDefined(s)) ||
            draft.HelpCamera is not ("bench" or "gripper" or "depth") || Path.GetFileName(draft.ReportFile) != draft.ReportFile ||
            draft.ReportFile.Length > 160 || draft.ReportFile.Length > 0 && !draft.ReportFile.EndsWith("-calibration.json", StringComparison.Ordinal))
            throw new InvalidOperationException("Saved wizard progress is invalid. Keep its report and start a new draft.");
    }
}

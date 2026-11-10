using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Provenance for recomputed scene geometry; physical observations stay unchanged.</summary>
public sealed record CalibrationModelReevaluation(string PreviousModelRevision, string ModelRevision,
    string AuditFile, DateTimeOffset EvaluatedAt, string Evidence =
    "Saved physical angles evaluated by the engine in private FK data. Scene points are derived; original capture fingerprints and times still identify the original observations.",
    string PreviousSceneId = "", string SceneId = "");

public static class CalibrationModelEvaluation
{
    private static readonly string[] ArmJoints = Enumerable.Range(1, 6).Select(i => "joint" + i).ToArray();

    public static JsonObject JointInputs(CalibrationSample sample)
    {
        if (sample.JointDegrees is not { Length: 6 } || sample.JointDegrees.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("The saved observation needs six finite measured joint angles.");
        var inputs = new JsonObject();
        for (int i = 0; i < ArmJoints.Length; i++) inputs[ArmJoints[i]] = sample.JointDegrees[i] * Math.PI / 180;
        return inputs;
    }

    public static void ValidateModel(JsonObject pose, CalibrationIdentity identity, CalibrationPlacement root)
    {
        if (pose["sceneId"]?.GetValue<string>() != identity.SceneId || pose["entityId"]?.GetValue<string>() != identity.RobotId ||
            pose["modelAssetId"]?.GetValue<string>() != identity.ModelAssetId || pose["modelRevision"]?.GetValue<string>() != identity.ModelRevision ||
            pose["metricFrame"]?.GetValue<string>() != "engine-world-y-up" || !root.Same(CalibrationPlacement.Read(pose["rootPlacement"])))
            throw new InvalidOperationException("The model, scene or original root changed during re-evaluation. Saved observations were not replaced.");
    }

    public static CalibrationPoint Read(JsonObject result, CalibrationIdentity identity, CalibrationPlacement root,
        string siteName, CalibrationSample sample)
    {
        ValidateModel(result, identity, root);
        if (result["evaluationOnly"]?.GetValue<bool>() != true || result["joints"] is not JsonArray joints || joints.Count != ArmJoints.Length)
            throw new InvalidOperationException("The engine did not acknowledge a private, read-only FK evaluation.");
        var inputs = JointInputs(sample);
        foreach (string name in ArmJoints)
        {
            var matches = joints.OfType<JsonObject>().Where(j => j["name"]?.GetValue<string>() == name).ToArray();
            if (matches.Length != 1 || matches[0]["unit"]?.GetValue<string>() != "rad" ||
                matches[0]["position"] is not JsonValue value || !value.TryGetValue<double>(out double angle) ||
                !double.IsFinite(angle) || Math.Abs(angle - inputs[name]!.GetValue<double>()) > 1e-9)
                throw new InvalidOperationException("The evaluated joints do not match the saved physical observation.");
        }
        var sites = result["toolSites"] is JsonArray toolSites ? toolSites.OfType<JsonObject>().ToArray() : [];
        if (sites.Length != 1 || sites[0]["name"]?.GetValue<string>() != siteName || sites[0]["unit"]?.GetValue<string>() != "m" ||
            sites[0]["position"] is not JsonArray { Count: 3 } xyz || sites[0]["requiredJoints"] is not JsonArray required ||
            !required.Select(j => j?.GetValue<string>()).SequenceEqual(ArmJoints))
            throw new InvalidOperationException("The named contact point must depend on exactly the six saved arm joints; no live gripper or missing joint state may be borrowed.");
        var point = new CalibrationPoint(xyz[0]!.GetValue<double>(), xyz[1]!.GetValue<double>(), xyz[2]!.GetValue<double>());
        if (!point.Finite) throw new InvalidOperationException("The evaluated named contact point is not finite.");
        return point;
    }
}

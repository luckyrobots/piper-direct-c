using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

/// <summary>An operator-confirmed display reference, never a motor/encoder calibration.</summary>
public sealed record PikaClosedDisplayReference(int Schema, string ProfileId, string Device, string? Distribution,
    double MotorAngle, DateTimeOffset ConfirmedAt)
{
    public const double MaximumNominalOffsetMm = 5;
    /// <summary>A closed-jaw encoder reading farther than this from zero is not a display offset.</summary>
    public const double MaximumMotorAngleRad = .15;
    public bool Valid => Schema == 1 && !string.IsNullOrEmpty(ProfileId) && !string.IsNullOrEmpty(Device) &&
        double.IsFinite(MotorAngle) && Math.Abs(MotorAngle) <= MaximumMotorAngleRad &&
        Math.Abs(PikaProtocol.Width(MotorAngle)) <= MaximumNominalOffsetMm;

    public bool Matches(string profile, string device, string? distribution, JsonObject? pika) => Valid &&
        ProfileId == profile && Device == device && Distribution == distribution &&
        pika?["device"]?.GetValue<string>() == device &&
        (distribution is null || pika["distribution"]?.GetValue<string>() == distribution) &&
        pika["connected"]?.GetValue<bool>() == true;

    public static string Blocker(string profile, string device, string? distribution, JsonObject? pika, bool armMoving)
    {
        if (string.IsNullOrEmpty(profile) || string.IsNullOrEmpty(device) || pika?["device"]?.GetValue<string>() != device ||
            distribution is not null && pika?["distribution"]?.GetValue<string>() != distribution)
            return "Assign this robot's Pika device first.";
        if (pika?["connected"]?.GetValue<bool>() != true || pika["fresh"]?.GetValue<bool>() != true ||
            pika["telemetry"]?["motor"]?["Position"] is not JsonValue position || !position.TryGetValue<double>(out double angle) || !double.IsFinite(angle))
            return "Waiting for fresh connected gripper feedback.";
        if (armMoving || pika["pending"]?.GetValue<bool>() == true ||
            pika["telemetry"]?["motor"]?["Speed"] is not JsonValue speed || !speed.TryGetValue<double>(out double velocity) ||
            !double.IsFinite(velocity) || Math.Abs(velocity) > PikaProtocol.StationarySpeedRadiansPerSecond)
            return "Wait until the arm and gripper are stationary.";
        if (!new PikaClosedDisplayReference(1, profile, device, distribution, angle, DateTimeOffset.UtcNow).Valid)
            return "The encoder is too far from nominal closed (more than 5 mm). Check the gripper instead of redefining a large offset.";
        return "";
    }

    public static PikaClosedDisplayReference Capture(string profile, string device, string? distribution, JsonObject? pika, bool armMoving)
    {
        string reason = Blocker(profile, device, distribution, pika, armMoving);
        if (reason.Length > 0) throw new InvalidOperationException(reason);
        return new(1, profile, device, distribution, pika!["telemetry"]!["motor"]!["Position"]!.GetValue<double>(), DateTimeOffset.UtcNow);
    }

    internal static PikaDisplayReferenceKey Key(RobotProfile profile) => new(profile.Id, profile.PikaDevice, profile.PikaDistribution);
    internal static string PathFor(string settingsDirectory, string profileId) =>
        Path.Combine(settingsDirectory, "gripper-reference", profileId + ".json");

    /// <summary>The saved reference for exactly this profile's current Pika
    /// assignment. A missing, unreadable or foreign file reads as none.</summary>
    internal static PikaClosedDisplayReference? TryLoad(string settingsDirectory, RobotProfile profile)
    {
        string path = PathFor(settingsDirectory, profile.Id);
        try
        {
            if (!File.Exists(path)) return null;
            var saved = JsonSerializer.Deserialize<PikaClosedDisplayReference>(File.ReadAllText(path), PluginProtocol.Json);
            // Files written before null meant native Linux stored "".
            if (saved is { Distribution: "" }) saved = saved with { Distribution = null };
            return saved is { Valid: true } && saved.ProfileId == profile.Id && saved.Device == profile.PikaDevice &&
                saved.Distribution == profile.PikaDistribution ? saved : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { Trace.TraceWarning("Pika display reference: {0}", error.Message); return null; }
    }

    /// <summary>The reference angle when it applies to this profile and live Pika.</summary>
    internal double? AngleFor(RobotProfile profile, JsonObject? pika) =>
        Matches(profile.Id, profile.PikaDevice, profile.PikaDistribution, pika) ? MotorAngle : null;
}

/// <summary>Identity of the Pika assignment a saved display reference belongs to.</summary>
internal readonly record struct PikaDisplayReferenceKey(string ProfileId, string Device, string? Distribution);

public sealed partial class PipersPlugin
{
    internal PikaDisplayReferenceKey? pikaDisplayReferenceKey;
    internal PikaClosedDisplayReference? pikaDisplayReference;
    private string PikaReferencePath => PikaClosedDisplayReference.PathFor(engine.Start.SettingsDirectory, ActiveProfile.Id);

    private void LoadPikaDisplayReference()
    {
        var key = PikaClosedDisplayReference.Key(ActiveProfile);
        if (pikaDisplayReferenceKey == key) return;
        pikaDisplayReferenceKey = key;
        pikaDisplayReference = PikaClosedDisplayReference.TryLoad(engine.Start.SettingsDirectory, ActiveProfile);
    }

    private double? PikaDisplayReferenceAngle(JsonObject? pika)
    {
        LoadPikaDisplayReference();
        return pikaDisplayReference?.AngleFor(ActiveProfile, pika);
    }

    private bool PikaDisplayOpening(JsonObject? pika, out double width) =>
        GripperLink.TryMeasuredOpening(pika, out width, PikaDisplayReferenceAngle(pika));

    private Widget PikaReferenceControls() => Widget.Column("pika.reference.controls",
        Widget.Label("pika.reference.title", "Gripper display reference") with { Appearance = "heading" },
        Widget.Label("pika.reference.status", "") with { Height = 52 },
        Widget.Row("pika.reference.actions", Widget.Button("pika.reference.set", "Use current as closed") with
        { Hint = "With the physical jaws fully closed and stationary, save this encoder reading as zero opening for the scene and readout. Sends no command and does not change motor targets or encoder calibration." },
            Widget.Button("pika.reference.clear", "Clear display reference")),
        Widget.Label("pika.reference.help", "Fully close the physical jaws before setting this reference. It changes the scene/readout only. Recheck after changing the gripper; full-travel accuracy still needs verification.") with { Height = 48, Appearance = "muted" });

    private async Task PikaReferenceEvent(string action, CancellationToken cancellation)
    {
        if (action == "set")
        {
            if (!TelemetryIsFresh()) throw new InvalidOperationException("Waiting for fresh controller telemetry.");
            var saved = PikaClosedDisplayReference.Capture(ActiveProfile.Id, ActiveProfile.PikaDevice, ActiveProfile.PikaDistribution,
                state?["pika"] as JsonObject, state?["motion"]?["active"]?.GetValue<bool>() == true);
            string path = PikaReferencePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(saved, PluginProtocol.Json), cancellation);
            File.Move(path + ".tmp", path, true);
            pikaDisplayReference = saved;
            pikaDisplayReferenceKey = PikaClosedDisplayReference.Key(ActiveProfile);
            message = "Closed display reference saved. The scene and readout use this position as 0 mm; motor targets and encoder calibration are unchanged.";
        }
        else if (action == "clear")
        {
            if (File.Exists(PikaReferencePath)) File.Delete(PikaReferencePath);
            pikaDisplayReference = null;
            pikaDisplayReferenceKey = PikaClosedDisplayReference.Key(ActiveProfile);
            message = "Gripper display reference cleared. The nominal encoder reference is used again; no hardware command was sent.";
        }
        else throw new InvalidOperationException("Unknown gripper display-reference action.");
        await Update(cancellation);
    }

    private void AddPikaReferencePatches(Dictionary<string, JsonObject> patches, JsonObject? pika)
    {
        double? reference = PikaDisplayReferenceAngle(pika);
        string reason = PikaClosedDisplayReference.Blocker(ActiveProfile.Id, ActiveProfile.PikaDevice, ActiveProfile.PikaDistribution,
            pika, state?["motion"]?["active"]?.GetValue<bool>() == true);
        bool reading = PikaDisplayOpening(pika, out _);
        string raw = pika?["telemetry"]?["motor"]?["Position"]?.GetValue<double>().ToString("F5") ?? "—";
        patches["pika.reference.status"] = new() { ["text"] = reference.HasValue
            ? $"Closed display reference: {reference.Value:F5} rad · current raw encoder: {raw} rad. Scene/readout only; full-travel calibration unverified."
            : $"Nominal reference · raw encoder: {raw} rad. " + (!reading && reason.Length == 0 ? "Jaws closed? Use current as closed to align the scene/readout." : reason) };
        patches["pika.reference.set"] = new() { ["enabled"] = TelemetryIsFresh() && reason.Length == 0,
            ["visible"] = Navigation.Page != WorkspacePage.Controls || !reading && reason.Length == 0,
            ["hint"] = reason.Length > 0 ? reason : "Only click with the physical jaws fully closed. Saves a display reference; sends no motion or encoder command." };
        patches["pika.reference.clear"] = new() { ["enabled"] = pikaDisplayReference is not null };
    }
}

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

/// <summary>An operator-confirmed display reference, never a motor/encoder calibration.</summary>
public sealed record PikaClosedDisplayReference(int Schema, string ProfileId, string Device, string Distribution,
    double MotorAngle, DateTimeOffset ConfirmedAt)
{
    public const double MaximumNominalOffsetMm = 5;
    public bool Valid => Schema == 1 && !string.IsNullOrEmpty(ProfileId) && !string.IsNullOrEmpty(Device) && Distribution is not null &&
        double.IsFinite(MotorAngle) && Math.Abs(MotorAngle) <= .15 &&
        Math.Abs(PikaProtocol.Width(MotorAngle)) <= MaximumNominalOffsetMm;

    public bool Matches(string profile, string device, string distribution, JsonObject? pika) => Valid &&
        ProfileId == profile && Device == device && Distribution == distribution &&
        pika?["device"]?.GetValue<string>() == device &&
        (distribution.Length == 0 || pika["distribution"]?.GetValue<string>() == distribution) &&
        pika["connected"]?.GetValue<bool>() == true;

    public static string Blocker(string profile, string device, string distribution, JsonObject? pika, bool armMoving)
    {
        if (string.IsNullOrEmpty(profile) || string.IsNullOrEmpty(device) || pika?["device"]?.GetValue<string>() != device ||
            distribution.Length > 0 && pika?["distribution"]?.GetValue<string>() != distribution)
            return "Assign this robot's Pika device first.";
        if (pika?["connected"]?.GetValue<bool>() != true || pika["fresh"]?.GetValue<bool>() != true ||
            pika["telemetry"]?["motor"]?["Position"] is not JsonValue position || !position.TryGetValue<double>(out double angle) || !double.IsFinite(angle))
            return "Waiting for fresh connected gripper feedback.";
        if (armMoving || pika["pending"]?.GetValue<bool>() == true ||
            pika["telemetry"]?["motor"]?["Speed"] is not JsonValue speed || !speed.TryGetValue<double>(out double velocity) ||
            !double.IsFinite(velocity) || Math.Abs(velocity) > .02)
            return "Wait until the arm and gripper are stationary.";
        if (!new PikaClosedDisplayReference(1, profile, device, distribution, angle, DateTimeOffset.UtcNow).Valid)
            return "The encoder is too far from nominal closed (more than 5 mm). Check the gripper instead of redefining a large offset.";
        return "";
    }

    public static PikaClosedDisplayReference Capture(string profile, string device, string distribution, JsonObject? pika, bool armMoving)
    {
        string reason = Blocker(profile, device, distribution, pika, armMoving);
        if (reason.Length > 0) throw new InvalidOperationException(reason);
        return new(1, profile, device, distribution, pika!["telemetry"]!["motor"]!["Position"]!.GetValue<double>(), DateTimeOffset.UtcNow);
    }
}

public sealed partial class PipersPlugin
{
    private string pikaDisplayReferenceKey = "";
    private PikaClosedDisplayReference? pikaDisplayReference;
    private string PikaReferenceDistribution => OperatingSystem.IsWindows() ? ActiveProfile.WslDistribution : "";
    private string PikaReferencePath => Path.Combine(engine.Start.SettingsDirectory, "gripper-reference", ActiveProfile.Id + ".json");

    private void LoadPikaDisplayReference()
    {
        string key = ActiveProfile.Id + "\n" + ActiveProfile.PikaDevice + "\n" + PikaReferenceDistribution;
        if (pikaDisplayReferenceKey == key) return;
        pikaDisplayReferenceKey = key; pikaDisplayReference = null;
        try
        {
            if (File.Exists(PikaReferencePath))
            {
                var saved = JsonSerializer.Deserialize<PikaClosedDisplayReference>(File.ReadAllText(PikaReferencePath), PluginProtocol.Json);
                if (saved is { Valid: true } && saved.ProfileId == ActiveProfile.Id && saved.Device == ActiveProfile.PikaDevice && saved.Distribution == PikaReferenceDistribution)
                    pikaDisplayReference = saved;
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
        { Trace.TraceWarning("Pika display reference: {0}", error.Message); }
    }

    private double? PikaDisplayReferenceAngle(JsonObject? pika)
    {
        LoadPikaDisplayReference();
        return pikaDisplayReference?.Matches(ActiveProfile.Id, ActiveProfile.PikaDevice, PikaReferenceDistribution, pika) == true
            ? pikaDisplayReference.MotorAngle : null;
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
            var saved = PikaClosedDisplayReference.Capture(ActiveProfile.Id, ActiveProfile.PikaDevice, PikaReferenceDistribution,
                state?["pika"] as JsonObject, state?["motion"]?["active"]?.GetValue<bool>() == true);
            string path = PikaReferencePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(saved, PluginProtocol.Json), cancellation);
            File.Move(path + ".tmp", path, true);
            pikaDisplayReference = saved;
            pikaDisplayReferenceKey = ActiveProfile.Id + "\n" + ActiveProfile.PikaDevice + "\n" + PikaReferenceDistribution;
            message = "Closed display reference saved. The scene and readout use this position as 0 mm; motor targets and encoder calibration are unchanged.";
        }
        else if (action == "clear")
        {
            if (File.Exists(PikaReferencePath)) File.Delete(PikaReferencePath);
            pikaDisplayReference = null;
            pikaDisplayReferenceKey = ActiveProfile.Id + "\n" + ActiveProfile.PikaDevice + "\n" + PikaReferenceDistribution;
            message = "Gripper display reference cleared. The nominal encoder reference is used again; no hardware command was sent.";
        }
        else throw new InvalidOperationException("Unknown gripper display-reference action.");
        await Update(cancellation);
    }

    private void AddPikaReferencePatches(Dictionary<string, JsonObject> patches, JsonObject? pika)
    {
        double? reference = PikaDisplayReferenceAngle(pika);
        string reason = PikaClosedDisplayReference.Blocker(ActiveProfile.Id, ActiveProfile.PikaDevice, PikaReferenceDistribution,
            pika, state?["motion"]?["active"]?.GetValue<bool>() == true);
        bool reading = PikaDisplayOpening(pika, out _);
        string raw = pika?["telemetry"]?["motor"]?["Position"]?.GetValue<double>().ToString("F5") ?? "—";
        patches["pika.reference.status"] = new() { ["text"] = reference.HasValue
            ? $"Closed display reference: {reference.Value:F5} rad · current raw encoder: {raw} rad. Scene/readout only; full-travel calibration unverified."
            : $"Nominal reference · raw encoder: {raw} rad. " + (!reading && reason.Length == 0 ? "Jaws closed? Use current as closed to align the scene/readout." : reason) };
        patches["pika.reference.set"] = new() { ["enabled"] = TelemetryIsFresh() && reason.Length == 0,
            ["visible"] = workspacePage != "controls" || !reading && reason.Length == 0,
            ["hint"] = reason.Length > 0 ? reason : "Only click with the physical jaws fully closed. Saves a display reference; sends no motion or encoder command." };
        patches["pika.reference.clear"] = new() { ["enabled"] = pikaDisplayReference is not null };
    }
}

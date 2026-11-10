using System.Collections.Concurrent;
using System.Diagnostics;
using LuckyRobots.Piper.Processes;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

// Lens calibration session (Pika fisheye), distinct from desk calibration.
//   NoSession -> Capturing        first board capture creates a session folder
//   Capturing -> Capturing        more captures
//   Capturing|Rejected -> Solving solve starts on the lens worker
//   Solving -> Saved              profile written; the session closes
//   Solving -> Rejected           the session stays open for more captures
//   any -> NoSession              New session, or a robot profile switch
// A session folder exists exactly in Capturing, Solving and Rejected.
internal enum LensPhase { NoSession, Capturing, Solving, Saved, Rejected }

internal sealed record LensSession(LensPhase Phase, string? Folder, long LastSequence, string Status)
{
    public static readonly LensSession Initial = new(LensPhase.NoSession, null, 0, "No lens calibration. Camera mount remains unverified.");
    public bool Open => Phase is LensPhase.Capturing or LensPhase.Solving or LensPhase.Rejected;

    public LensSession WithStatus(string status) => this with { Status = status };
    public LensSession New() => new(LensPhase.NoSession, null, 0, "New capture session ready. Previous captures and the active profile are preserved.");
    public LensSession Captured(string folder, long sequence, int count) =>
        Valid(new(LensPhase.Capturing, folder, sequence, $"{count} captures saved. Aim for 20+ varied poses, then solve."));
    public LensSession Solving() => Valid(this with { Phase = LensPhase.Solving, Status = "Solving lens and checking held-out images…" });
    public LensSession Saved(double heldOutRmsPixels) =>
        new(LensPhase.Saved, null, LastSequence, $"Lens saved · held-out RMS {heldOutRmsPixels:F2} px. Mount alignment still required.");
    public LensSession Rejected(string reason) => Valid(this with { Phase = LensPhase.Rejected, Status = "Calibration not accepted: " + reason });

    private static LensSession Valid(LensSession next)
    {
        if (next.Open != next.Folder is not null)
            throw new InvalidOperationException("The lens calibration session has no capture folder for this step. Nothing was changed; start a new session.");
        return next;
    }
}

public sealed partial class PipersPlugin
{
    private LensSession lens => calibration.Lens.Session;

    private string LensCalibrationDirectory => Path.Combine(engine.Start.SettingsDirectory, "camera-calibration", ActiveProfile.Id);
    private bool LensWorkerBusy => calibration.Lens.WorkerBusy;

    private async Task LoadCameraCalibration(CancellationToken cancellation)
    {
        string path = Path.Combine(LensCalibrationDirectory, "gripper.json");
        string legacy = Path.Combine(engine.Start.SettingsDirectory, "camera-calibration", "gripper.json");
        if (ActiveProfile.Id == "piper-1" && !File.Exists(path) && File.Exists(legacy))
        {
            Directory.CreateDirectory(LensCalibrationDirectory);
            File.Copy(legacy, path);
        }
        if (!File.Exists(path)) return;
        try
        {
            var value = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellation))!.AsObject();
            Materialize(value);
            calibration.Lens.InstallProfile("gripper", value);
            calibration.Lens.Report("Saved lens profile loaded. Mount alignment still required.");
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException)
        { calibration.Lens.Report("Saved lens profile is invalid; capture a new calibration."); }
    }

    private async Task CameraCalibrationEvent(string id, string action, CancellationToken cancellation)
    {
        if (id != "gripper") throw new InvalidOperationException("Use device factory intrinsics for depth; this workflow calibrates the Pika fisheye.");
        Directory.CreateDirectory(LensCalibrationDirectory);
        if (action == "calibrationboard")
        {
            string destination = Path.Combine(LensCalibrationDirectory, "checkerboard-20mm.svg");
            File.Copy(Path.Combine(engine.Start.PackageDirectory, "camera", "checkerboard.svg"), destination, true);
            Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
            return;
        }
        if (LensWorkerBusy) throw new InvalidOperationException("Camera setup or calibration is still running.");
        if (action == "calibrationtools")
        {
            calibration.Lens.Report("Checking the bundled camera runtime…");
            calibration.Lens.Run(SetUpCameraTools(shutdown.Token));
            return;
        }
        if (action == "calibrationnew")
        {
            calibration.Lens.New();
            return;
        }
        var frame = cameras.Get(id)?.LatestFrame ?? throw new InvalidOperationException("Wait for a camera frame first.");
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - frame.TimestampUnixMicroseconds / 1000 > 1000)
            throw new InvalidOperationException("The camera frame is stale. Reconnect the camera first.");
        if (action == "calibrationcapture")
        {
            if (frame.Sequence == lens.LastSequence) throw new InvalidOperationException("Wait for a new frame and change the board pose.");
            string folder = lens.Folder ?? Path.Combine(LensCalibrationDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            if (lens.Folder is null)
            {
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(Path.Combine(folder, "session.json"), new JsonObject {
                    ["device"] = frame.DeviceId, ["width"] = frame.Width, ["height"] = frame.Height,
                    ["squareMetres"] = .02, ["columns"] = 7, ["rows"] = 5 }.ToJsonString(), cancellation);
            }
            var session = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "session.json"), cancellation))!;
            if (session["device"]!.GetValue<string>() != frame.DeviceId || session["width"]!.GetValue<int>() != frame.Width || session["height"]!.GetValue<int>() != frame.Height)
                throw new InvalidOperationException("Camera/resolution changed. Choose New capture session before capturing again.");
            // A camera worker restart resets its sequence. Keep prior captures intact.
            string captureName = $"frame-{frame.TimestampUnixMicroseconds:D18}-{frame.Sequence:D8}-{Guid.NewGuid():N}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(folder, captureName), Convert.FromBase64String(frame.JpegBase64), cancellation);
            calibration.Lens.Captured(folder, frame.Sequence, Directory.GetFiles(folder, "frame-*.jpg").Length);
        }
        else if (action == "calibrationsolve")
        {
            if (lens.Folder is not { } folder) throw new InvalidOperationException("Capture at least 20 board images first.");
            calibration.Lens.BeginSolve();
            calibration.Lens.Run(SolveCameraCalibration(folder, shutdown.Token));
        }
    }

    private async Task SetUpCameraTools(CancellationToken cancellation)
    {
        try
        {
            var result = await RunCameraTool(["check"], TimeSpan.FromSeconds(15), cancellation);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
            if (JsonNode.Parse(result.Output)?["ready"]?.GetValue<bool>() != true) throw new InvalidDataException("Invalid camera runtime response.");
            calibration.Lens.Report("Bundled camera runtime ready. No Python required. Open the board and capture varied views.");
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        { calibration.Lens.Report("Camera runtime: " + error.Message + " Reinstall the Pipers package if a bundled library is missing."); }
    }

    private Task<BoundedProcess.Result> RunCameraTool(string[] arguments, TimeSpan timeout, CancellationToken cancellation) =>
        CameraRuntime.RunAsync(engine.Start.PackageDirectory, arguments, timeout, cancellation);

    private async Task SolveCameraCalibration(string session, CancellationToken cancellation)
    {
        try
        {
            var result = await RunCameraTool(["calibrate", "--session", session], TimeSpan.FromMinutes(5), cancellation);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
            var profile = JsonNode.Parse(result.Output)!.AsObject();
            Materialize(profile);
            var capture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(session, "session.json"), cancellation))!;
            if (!CameraProfile.MatchesFisheye(profile, capture["device"]!.GetValue<string>(), capture["width"]!.GetValue<int>(), capture["height"]!.GetValue<int>()))
                throw new InvalidOperationException("Solver returned an invalid or mismatched camera profile.");
            string path = Path.Combine(LensCalibrationDirectory, "gripper.json");
            await File.WriteAllTextAsync(path + ".tmp", profile.ToJsonString(), cancellation);
            File.Move(path + ".tmp", path, true);
            calibration.Lens.InstallProfile("gripper", profile);
            calibration.Lens.Saved(profile["validationRmsPixels"]!.GetValue<double>());
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        { calibration.Lens.Rejected(error.Message); }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private readonly ConcurrentDictionary<string, JsonObject> cameraCalibration = new();
    private string calibrationStatus = "No lens calibration. Camera mount remains unverified.";
    private string? calibrationSession;
    private long calibrationLastSequence;
    private Task? calibrationWorker;

    private string CalibrationDirectory => Path.Combine(engine.Start.SettingsDirectory, "camera-calibration");

    private async Task LoadCameraCalibration(CancellationToken cancellation)
    {
        string path = Path.Combine(CalibrationDirectory, "gripper.json");
        if (!File.Exists(path)) return;
        try
        {
            var value = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellation))!.AsObject();
            Materialize(value);
            cameraCalibration["gripper"] = value;
            calibrationStatus = "Saved lens profile loaded. Mount alignment still required.";
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException)
        { calibrationStatus = "Saved lens profile is invalid; capture a new calibration."; }
    }

    private async Task CameraCalibrationEvent(string id, string action, CancellationToken cancellation)
    {
        if (id != "gripper") throw new InvalidOperationException("Use device factory intrinsics for depth; this workflow calibrates the Pika fisheye.");
        Directory.CreateDirectory(CalibrationDirectory);
        if (action == "calibrationboard")
        {
            string destination = Path.Combine(CalibrationDirectory, "checkerboard-20mm.svg");
            File.Copy(Path.Combine(engine.Start.PackageDirectory, "camera", "checkerboard.svg"), destination, true);
            Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
            return;
        }
        if (calibrationWorker is { IsCompleted: false }) throw new InvalidOperationException("Camera setup or calibration is still running.");
        if (action == "calibrationtools")
        {
            calibrationStatus = "Checking the bundled camera runtime…";
            calibrationWorker = SetUpCameraTools(shutdown.Token);
            return;
        }
        if (action == "calibrationnew")
        {
            calibrationSession = null;
            calibrationLastSequence = 0;
            calibrationStatus = "New capture session ready. Previous captures and the active profile are preserved.";
            return;
        }
        var frame = cameras.GetValueOrDefault(id)?.LatestFrame ?? throw new InvalidOperationException("Wait for a camera frame first.");
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - frame.TimestampUnixMicroseconds / 1000 > 1000)
            throw new InvalidOperationException("The camera frame is stale. Reconnect the camera first.");
        if (action == "calibrationcapture")
        {
            if (frame.Sequence == calibrationLastSequence) throw new InvalidOperationException("Wait for a new frame and change the board pose.");
            if (calibrationSession is null)
            {
                calibrationSession = Path.Combine(CalibrationDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(calibrationSession);
                await File.WriteAllTextAsync(Path.Combine(calibrationSession, "session.json"), new JsonObject {
                    ["device"] = frame.DeviceId, ["width"] = frame.Width, ["height"] = frame.Height,
                    ["squareMetres"] = .02, ["columns"] = 7, ["rows"] = 5 }.ToJsonString(), cancellation);
            }
            var session = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(calibrationSession, "session.json"), cancellation))!;
            if (session["device"]!.GetValue<string>() != frame.DeviceId || session["width"]!.GetValue<int>() != frame.Width || session["height"]!.GetValue<int>() != frame.Height)
                throw new InvalidOperationException("Camera/resolution changed. Choose New capture session before capturing again.");
            // A camera worker restart resets its sequence. Keep prior captures intact.
            string captureName = $"frame-{frame.TimestampUnixMicroseconds:D18}-{frame.Sequence:D8}-{Guid.NewGuid():N}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(calibrationSession, captureName), Convert.FromBase64String(frame.JpegBase64), cancellation);
            calibrationLastSequence = frame.Sequence;
            calibrationStatus = $"{Directory.GetFiles(calibrationSession, "frame-*.jpg").Length} captures saved. Aim for 20+ varied poses, then solve.";
        }
        else if (action == "calibrationsolve")
        {
            if (calibrationSession is null) throw new InvalidOperationException("Capture at least 20 board images first.");
            string session = calibrationSession;
            calibrationStatus = "Solving lens and checking held-out images…";
            calibrationWorker = SolveCameraCalibration(session, shutdown.Token);
        }
    }

    private async Task SetUpCameraTools(CancellationToken cancellation)
    {
        try
        {
            var result = await RunCameraTool(["check"], TimeSpan.FromSeconds(15), cancellation);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
            if (JsonNode.Parse(result.Output)?["ready"]?.GetValue<bool>() != true) throw new InvalidDataException("Invalid camera runtime response.");
            calibrationStatus = "Bundled camera runtime ready. No Python required. Open the board and capture varied views.";
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        { calibrationStatus = "Camera runtime: " + error.Message + " Reinstall the Pipers package if a bundled library is missing."; }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunCameraTool(string[] arguments, TimeSpan timeout, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        var start = CameraWorker.StartInfo(engine.Start.PackageDirectory);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start bundled camera tools.");
        process.StandardInput.Close();
        using var stop = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            string error = await errors;
            return (process.ExitCode, await output, error.Length > 1000 ? error[^1000..] : error);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("Camera operation timed out; its worker was stopped."); }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
        }
    }

    private async Task SolveCameraCalibration(string session, CancellationToken cancellation)
    {
        try
        {
            var result = await RunCameraTool(["calibrate", "--session", session], TimeSpan.FromMinutes(5), cancellation);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
            var profile = JsonNode.Parse(result.Output)!.AsObject();
            Materialize(profile);
            var capture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(session, "session.json"), cancellation))!;
            if (!CameraProfile.Matches(profile, capture["device"]!.GetValue<string>(), capture["width"]!.GetValue<int>(), capture["height"]!.GetValue<int>(), false))
                throw new InvalidOperationException("Solver returned an invalid or mismatched camera profile.");
            string path = Path.Combine(CalibrationDirectory, "gripper.json");
            await File.WriteAllTextAsync(path + ".tmp", profile.ToJsonString(), cancellation);
            File.Move(path + ".tmp", path, true);
            cameraCalibration["gripper"] = profile;
            calibrationStatus = $"Lens saved · held-out RMS {profile["validationRmsPixels"]!.GetValue<double>():F2} px. Mount alignment still required.";
            calibrationSession = null;
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        { calibrationStatus = "Calibration not accepted: " + error.Message; }
    }
}

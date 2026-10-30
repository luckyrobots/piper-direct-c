using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using LuckyRobots.Piper.Camera;

// Camera-only worker: deliberately has no robot, transport or controller reference.
try
{
    string mode = args.FirstOrDefault() ?? "check";
    string Option(string name, string fallback = "")
    {
        int index = Array.IndexOf(args, "--" + name);
        return index < 0 ? fallback : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing " + name);
    }
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Cv2.SetNumThreads(2);
    if (mode == "check")
    {
        using var sample = new Mat(8, 8, MatType.CV_8UC1, Scalar.Black);
        if (!Cv2.ImEncode(".jpg", sample, out var encoded) || encoded.Length == 0) throw new InvalidOperationException("JPEG encoding unavailable.");
        string library = OperatingSystem.IsWindows() ? "realsense2.dll" : "librealsense2.so";
        var handle = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, library));
        NativeLibrary.Free(handle);
        int apiVersion = Intel.RealSense.Context.ApiVersion; // No context/device is opened.
        if (apiVersion != 25901) throw new InvalidOperationException("Unexpected bundled RealSense API version: " + apiVersion);
        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, runtime = ".NET", opencv = Cv2.GetVersionString(), realsenseApi = apiVersion, pythonRequired = false }));
    }
    else if (mode == "calibrate")
    {
        string session = Option("session");
        var result = FisheyeCalibration.Solve(session);
        File.WriteAllText(Path.Combine(session, "lens-profile.json"), result.ToJsonString(new() { WriteIndented = true }));
        Console.WriteLine(result.ToJsonString());
    }
    else if (mode == "depth") await DepthPreview.Run(Option("serial"), cancellation.Token);
    else if (mode == "color") await ColorPreview.Run(Option("device"), Option("size"), int.Parse(Option("fps", "0")), Option("codec"), cancellation.Token);
    else if (mode == "devices") Console.WriteLine(JsonSerializer.Serialize(VideoDevices.List()));
    else throw new ArgumentException("Unknown camera operation: " + mode);
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

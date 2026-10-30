using System.Text.Json.Nodes;
using LuckyRobots.Piper.Camera;
using OpenCvSharp;
using Intel.RealSense;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Reject(Action action, string expected)
{
    try { action(); }
    catch (InvalidDataException error) when (error.Message.Contains(expected, StringComparison.Ordinal)) { checks++; return; }
    throw new Exception("Expected rejection: " + expected);
}
Cv2.SetNumThreads(2);
Check(DepthPreview.Gray(0, .0001f) == 0 && DepthPreview.Gray(ushort.MaxValue, .0001f) == 0, "Invalid depth must stay black");
Check(DepthPreview.Gray(100, .0001f) == 0 && DepthPreview.Gray(6000, .0001f) == 0, "Depth outside 7–50 cm must stay black");
Check(DepthPreview.Gray(700, .0001f) >= 254 && DepthPreview.Gray(5000, .0001f) == 0, "Depth range endpoints changed");
Check(Math.Abs(DepthPreview.Gray(2850, .0001f) - 128) <= 1, "Metric depth midpoint is not linear");
Check(DepthPreview.Gray(1000, float.NaN) == 0, "Nonfinite depth scale accepted");
Check(DepthPreview.ValidateView("color") == "color" && DepthPreview.ValidateView("depth") == "depth", "D405 view selection changed");
bool invalidViewRejected = false;
try { DepthPreview.ValidateView("colorized"); } catch (ArgumentException) { invalidViewRejected = true; }
Check(invalidViewRejected, "An unknown D405 view was silently treated as another stream");
var factory = new Intrinsics { width = 640, height = 480, fx = 395, fy = 394, ppx = 317, ppy = 234,
    model = Distortion.InverseBrownConrady, coeffs = [-.053f, .055f, .00007f, .00068f, -.018f] };
var colorProfile = DepthPreview.FactoryProfile("d405-test", factory, "color");
Check(colorProfile["streamKind"]!.GetValue<string>() == "color" && colorProfile["model"]!.GetValue<string>() == "realsense-distorted", "Distorted D405 color was mislabeled rectified depth/pinhole");
Check(colorProfile["depthScale"] is null && colorProfile["distortionModel"]!.GetValue<string>() == "InverseBrownConrady" && colorProfile["coefficients"]!.AsArray().Count == 5, "Color metadata lost distortion or invented metric depth scale");
factory.coeffs = [0, 0, 0, 0, 0];
var depthProfile = DepthPreview.FactoryProfile("d405-test", factory, "depth", .0001f);
Check(depthProfile["streamKind"]!.GetValue<string>() == "depth" && depthProfile["model"]!.GetValue<string>() == "pinhole" && depthProfile["depthScale"]!.GetValue<float>() == .0001f, "Rectified depth factory scale/profile lost");
var rectifiedColor = DepthPreview.FactoryProfile("d405-test", factory, "color");
Check(rectifiedColor["streamKind"]!.GetValue<string>() == "color" && rectifiedColor["model"]!.GetValue<string>() == "pinhole" && rectifiedColor["depthScale"] is null, "Rectified color reused a depth profile/scale");
factory.model = Distortion.Ftheta;
Check(DepthPreview.FactoryProfile("d405-test", factory, "color")["model"]!.GetValue<string>() != "pinhole", "Zero fisheye coefficients were incorrectly classified as a pinhole image");
using (var image = new Mat(40, 64, MatType.CV_8UC1, Scalar.All(127)))
{
    Check(Cv2.ImEncode(".jpg", image, out var bytes), "Bundled JPEG encoder missing");
    using var decoded = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
    Check(decoded.Size() == new Size(64, 40), "JPEG changed camera aspect ratio");
}
using (var image = new Mat(40, 64, MatType.CV_8UC3, new Scalar(20, 90, 200)))
{
    Check(Cv2.ImEncode(".jpg", image, out var bytes), "Color JPEG encoding unavailable");
    using var decoded = Cv2.ImDecode(bytes, ImreadModes.Color);
    var pixel = decoded.At<Vec3b>(20, 30);
    Check(decoded.Size() == new Size(64, 40) && pixel.Item2 > pixel.Item1 && pixel.Item1 > pixel.Item0, "D405 BGR color preview became grayscale, swapped channels or changed aspect ratio");
}
string scratch = Path.Combine(Path.GetTempPath(), "piper-camera-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var metadata = new JsonObject { ["width"] = 640, ["height"] = 400, ["columns"] = 7, ["rows"] = 5, ["squareMetres"] = .02, ["device"] = "synthetic" };
    void WriteMetadata() => File.WriteAllText(Path.Combine(scratch, "session.json"), metadata.ToJsonString());
    WriteMetadata();
    Reject(() => FisheyeCalibration.Solve(scratch), "Only 0 usable boards");
    metadata["squareMetres"] = -1;
    WriteMetadata();
    Reject(() => FisheyeCalibration.Solve(scratch), "Invalid board session");
    metadata["squareMetres"] = .02;
    WriteMetadata();
    using (var wrongSize = new Mat(20, 30, MatType.CV_8UC1, Scalar.White)) Cv2.ImWrite(Path.Combine(scratch, "frame-00000000.jpg"), wrongSize);
    Reject(() => FisheyeCalibration.Solve(scratch), "same camera resolution");
    File.Delete(Path.Combine(scratch, "frame-00000000.jpg"));
    // All images contain a detectable board, but repeated poses must not pass.
    using (var narrow = new Mat(400, 640, MatType.CV_8UC1, Scalar.All(200)))
    {
        for (int y = 0; y < 6; y++) for (int x = 0; x < 8; x++) Cv2.Rectangle(narrow, new Rect(200 + 20 * x, 130 + 20 * y, 20, 20), (x + y) % 2 == 0 ? Scalar.Black : Scalar.White, -1);
        for (int i = 0; i < 20; i++) Cv2.ImWrite(Path.Combine(scratch, $"frame-{i:D8}.jpg"), narrow);
    }
    Reject(() => FisheyeCalibration.Solve(scratch), "coverage is too narrow");
    if (args.Length > 0)
    {
        // Optional end-to-end fixture: analytically rendered fisheye boards with
        // K=(245,247,315,198), D=(.015,-.003,.0002,0), 640x400.
        var result = FisheyeCalibration.Solve(args[0]);
        Check(result["device"]!.GetValue<string>() == "synthetic", "Device identity lost");
        Check(Math.Abs(result["fx"]!.GetValue<double>() - 245) < 3 && Math.Abs(result["fy"]!.GetValue<double>() - 247) < 3, "Fitted focal lengths differ from known synthetic lens");
        Check(Math.Abs(result["cx"]!.GetValue<double>() - 315) < 3 && Math.Abs(result["cy"]!.GetValue<double>() - 198) < 3, "Fitted optical centre differs from known synthetic lens");
        Check(result["validationWorstRmsPixels"]!.GetValue<double>() < 1, "Held-out image error too high");
        Check(result["mountVerified"]!.GetValue<bool>() == false, "Lens fit incorrectly claims mount calibration");
    }
}
finally { Directory.Delete(scratch, true); }
Console.WriteLine($"Passed {checks} camera runtime, depth and calibration checks. No camera or robot was opened.");

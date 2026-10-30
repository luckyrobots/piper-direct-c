using Intel.RealSense;
using OpenCvSharp;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Camera;

public static class DepthPreview
{
    public static byte Gray(ushort raw, float scale)
    {
        float metres = raw * scale;
        return raw is 0 or ushort.MaxValue || !float.IsFinite(metres) || metres < .07f || metres > .5f
            ? (byte)0 : (byte)Math.Clamp((int)MathF.Round((1 - (metres - .07f) / .43f) * 255), 0, 255);
    }

    public static string ValidateView(string view) => view is "color" or "depth" ? view
        : throw new ArgumentException("D405 view must be color or depth.");

    public static JsonObject FactoryProfile(string serial, Intrinsics intr, string view, float? scale = null)
    {
        ValidateView(view);
        bool rectified = IsPinhole(intr);
        return new JsonObject {
            ["schema"] = 1, ["model"] = rectified ? "pinhole" : "realsense-distorted", ["device"] = serial,
            ["streamKind"] = view, ["width"] = intr.width, ["height"] = intr.height,
            ["fx"] = intr.fx, ["fy"] = intr.fy, ["cx"] = intr.ppx, ["cy"] = intr.ppy,
            ["coefficients"] = new JsonArray(intr.coeffs.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["distortionModel"] = intr.model.ToString(), ["depthScale"] = view == "depth" ? scale : null,
            ["source"] = "device-factory", ["mountVerified"] = false
        };
    }

    private static bool IsPinhole(Intrinsics intr) => intr.model is Distortion.None or Distortion.BrownConrady or Distortion.ModifiedBrownConrady or Distortion.InverseBrownConrady &&
        intr.coeffs.Length == 5 && intr.coeffs.All(c => float.IsFinite(c) && Math.Abs(c) < 1e-8);

    public static Task Run(string selectedSerial, CancellationToken cancellation) => Run(selectedSerial, "depth", cancellation);

    public static async Task Run(string selectedSerial, string view, CancellationToken cancellation)
    {
        ValidateView(view);
        bool metricDepth = view == "depth";
        using var context = new Context();
        using var devices = context.QueryDevices();
        var serials = new List<string>();
        foreach (var device in devices)
            using (device)
                if (device.Info[CameraInfo.Name].Contains("D405", StringComparison.OrdinalIgnoreCase))
                {
                    string serial = device.Info[CameraInfo.SerialNumber];
                    if (selectedSerial.Length == 0 || serial == selectedSerial) serials.Add(serial);
                }
        if (serials.Count != 1) throw new InvalidOperationException("Select exactly one D405 by serial number in camera settings.");
        using var pipeline = new Pipeline(context);
        using var config = new Config();
        config.EnableDevice(serials[0]);
        var requestedStream = metricDepth ? Intel.RealSense.Stream.Depth : Intel.RealSense.Stream.Color;
        config.EnableStream(requestedStream, 640, 480, metricDepth ? Format.Z16 : Format.Bgr8, 15);
        using var started = pipeline.Start(config);
        try
        {
            using var stream = started.GetStream<VideoStreamProfile>(requestedStream);
            if (stream.Format != (metricDepth ? Format.Z16 : Format.Bgr8))
                throw new InvalidDataException("D405 returned a different stream format than the selected view.");
            var intr = stream.GetIntrinsics();
            float? scale = null;
            if (metricDepth)
            {
                if (!IsPinhole(intr)) throw new InvalidOperationException("Depth stream is not rectified; zero pinhole distortion is required for this preview.");
                using var frames = pipeline.WaitForFrames(3000);
                using var depth = frames.DepthFrame;
                scale = depth.GetUnits();
                if (!float.IsFinite(scale.Value) || scale <= 0) throw new InvalidDataException("Invalid factory depth scale.");
            }
            Console.Error.WriteLine("PIPER_CAMERA_PROFILE " + FactoryProfile(serials[0], intr, view, scale).ToJsonString());
            using var image = new Mat(intr.height, intr.width, metricDepth ? MatType.CV_8UC1 : MatType.CV_8UC3);
            using var output = Console.OpenStandardOutput();
            while (!cancellation.IsCancellationRequested)
            {
                using var frames = pipeline.WaitForFrames(3000);
                if (metricDepth)
                {
                    using var depth = frames.DepthFrame;
                    Fill(depth, image, scale!.Value);
                }
                else
                {
                    using var color = frames.ColorFrame ?? throw new IOException("D405 color stream returned no color frame.");
                    FillColor(color, image);
                }
                Cv2.ImEncode(".jpg", image, out var jpeg, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 85)]);
                await output.WriteAsync(jpeg, cancellation);
                await output.FlushAsync(cancellation);
            }
        }
        finally { pipeline.Stop(); }
    }

    private static unsafe void Fill(DepthFrame depth, Mat gray, float scale)
    {
        int rows = gray.Rows, cols = gray.Cols, stride = depth.Stride;
        var data = (byte*)depth.Data;
        if (depth.Width != cols || depth.Height != rows) throw new InvalidDataException("Depth resolution changed during capture.");
        for (int y = 0; y < rows; y++)
        {
            var raw = (ushort*)(data + y * stride);
            var row = (byte*)gray.Ptr(y);
            for (int x = 0; x < cols; x++) row[x] = Gray(raw[x], scale);
        }
    }

    private static unsafe void FillColor(VideoFrame frame, Mat image)
    {
        int rows = image.Rows, cols = image.Cols, stride = frame.Stride;
        if (frame.Width != cols || frame.Height != rows || stride < cols * 3)
            throw new InvalidDataException("D405 color resolution or BGR row layout changed during capture.");
        var data = (byte*)frame.Data;
        for (int y = 0; y < rows; y++)
            new ReadOnlySpan<byte>(data + y * stride, cols * 3)
                .CopyTo(new Span<byte>((void*)image.Ptr(y), cols * 3));
    }
}

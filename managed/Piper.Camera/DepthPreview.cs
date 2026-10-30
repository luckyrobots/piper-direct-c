using Intel.RealSense;
using OpenCvSharp;
using System.Text.Json;

namespace LuckyRobots.Piper.Camera;

public static class DepthPreview
{
    public static byte Gray(ushort raw, float scale)
    {
        float metres = raw * scale;
        return raw is 0 or ushort.MaxValue || !float.IsFinite(metres) || metres < .07f || metres > .5f
            ? (byte)0 : (byte)Math.Clamp((int)MathF.Round((1 - (metres - .07f) / .43f) * 255), 0, 255);
    }

    public static async Task Run(string selectedSerial, CancellationToken cancellation)
    {
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
        config.EnableStream(Intel.RealSense.Stream.Depth, 640, 480, Format.Z16, 15);
        using var started = pipeline.Start(config);
        try
        {
            using var stream = started.GetStream<VideoStreamProfile>(Intel.RealSense.Stream.Depth);
            var intr = stream.GetIntrinsics();
            if (intr.coeffs.Any(c => Math.Abs(c) > 1e-8)) throw new InvalidOperationException("Depth stream is not rectified; zero distortion is required for this preview.");
            float scale;
            using (var frames = pipeline.WaitForFrames(3000))
            using (var depth = frames.DepthFrame) scale = depth.GetUnits();
            if (!float.IsFinite(scale) || scale <= 0) throw new InvalidDataException("Invalid factory depth scale.");
            Console.Error.WriteLine("PIPER_CAMERA_PROFILE " + JsonSerializer.Serialize(new {
                schema = 1, model = "pinhole", device = serials[0], width = intr.width, height = intr.height,
                fx = intr.fx, fy = intr.fy, cx = intr.ppx, cy = intr.ppy, coefficients = intr.coeffs,
                depthScale = scale, source = "device-factory", mountVerified = false }));
            using var gray = new Mat(intr.height, intr.width, MatType.CV_8UC1);
            using var output = Console.OpenStandardOutput();
            while (!cancellation.IsCancellationRequested)
            {
                using var frames = pipeline.WaitForFrames(3000);
                using var depth = frames.DepthFrame;
                Fill(depth, gray, scale);
                Cv2.ImEncode(".jpg", gray, out var jpeg, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 85)]);
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
}

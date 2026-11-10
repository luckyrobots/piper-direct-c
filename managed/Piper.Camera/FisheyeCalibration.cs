using System.Text.Json.Nodes;
using OpenCvSharp;

namespace LuckyRobots.Piper.Camera;

/// <summary>Offline lens fitting with independent images held out of the fit.</summary>
public static class FisheyeCalibration
{
    public static JsonObject Solve(string folder)
    {
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "session.json")))!;
        var size = new Size(meta["width"]!.GetValue<int>(), meta["height"]!.GetValue<int>());
        var pattern = new Size(meta["columns"]!.GetValue<int>(), meta["rows"]!.GetValue<int>());
        double square = meta["squareMetres"]!.GetValue<double>();
        if (size.Width is < 16 or > 8192 || size.Height is < 16 or > 8192 || pattern.Width is < 3 or > 30 || pattern.Height is < 3 or > 30 || !double.IsFinite(square) || square <= 0)
            throw new InvalidDataException("Invalid board session dimensions.");
        var paths = Directory.GetFiles(folder, "frame-*.jpg").Order(StringComparer.Ordinal).ToArray();
        if (paths.Length > 500) throw new InvalidDataException("Use at most 500 captures per session.");
        int count = pattern.Width * pattern.Height;
        using var objects = new Mat(1, count, MatType.CV_64FC3);
        for (int y = 0; y < pattern.Height; y++)
            for (int x = 0; x < pattern.Width; x++) objects.Set(0, y * pattern.Width + x, new Vec3d(x * square, y * square, 0));
        var points = new List<Mat>();
        var names = new List<string>();
        try
        {
            foreach (string path in paths)
            {
                using var image = Cv2.ImRead(path, ImreadModes.Grayscale);
                if (image.Empty() || image.Size() != size) throw new InvalidDataException("All captures must use the same camera resolution.");
                if (!Cv2.FindChessboardCornersSB(image, pattern, out Point2f[] corners, ChessboardFlags.Exhaustive | ChessboardFlags.Accuracy)) continue;
                var observed = new Mat(1, count, MatType.CV_64FC2);
                for (int i = 0; i < count; i++) observed.Set(0, i, new Vec2d(corners[i].X, corners[i].Y));
                points.Add(observed);
                names.Add(Path.GetFileName(path));
            }
            if (points.Count < 20) throw new InvalidDataException($"Only {points.Count} usable boards; capture at least 20 sharp views with the entire checkerboard visible.");
            var all = points.SelectMany(p => Enumerable.Range(0, count).Select(i => p.At<Vec2d>(0, i))).ToArray();
            double[] extent = [(all.Max(p => p.Item0) - all.Min(p => p.Item0)) / size.Width, (all.Max(p => p.Item1) - all.Min(p => p.Item1)) / size.Height];
            if (extent.Any(e => e < .75)) throw new InvalidDataException("Board coverage is too narrow. Capture more boards near every image edge.");
            var centres = points.Select(p => new Vec2d(Enumerable.Range(0, count).Average(i => p.At<Vec2d>(0, i).Item0), Enumerable.Range(0, count).Average(i => p.At<Vec2d>(0, i).Item1))).ToArray();
            if ((centres.Max(p => p.Item0) - centres.Min(p => p.Item0)) / size.Width < .25 || (centres.Max(p => p.Item1) - centres.Min(p => p.Item1)) / size.Height < .25)
                throw new InvalidDataException("Move the board to different positions and tilts, not just one location.");
            var training = points.Where((_, i) => i % 5 != 0).ToArray();
            var validation = points.Where((_, i) => i % 5 == 0).ToArray();
            using var matrix = Mat.Eye(3, 3, MatType.CV_64FC1).ToMat();
            using var distortion = new Mat(4, 1, MatType.CV_64FC1, Scalar.All(0));
            double rms = Cv2.FishEye.Calibrate(Enumerable.Repeat(objects, training.Length), training, size, matrix, distortion,
                out var rotations, out var translations, FishEyeCalibrationFlags.RecomputeExtrinsic | FishEyeCalibrationFlags.CheckCond | FishEyeCalibrationFlags.FixSkew,
                new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 150, 1e-8));
            var normals = new List<Vec3d>();
            try
            {
                foreach (var rotation in rotations)
                {
                    using var r = new Mat();
                    Cv2.Rodrigues(rotation, r);
                    normals.Add(new(r.At<double>(0, 2), r.At<double>(1, 2), r.At<double>(2, 2)));
                }
            }
            finally { foreach (var r in rotations) r.Dispose(); foreach (var t in translations) t.Dispose(); }
            double Distance(Vec3d a, Vec3d b) => Math.Sqrt(Math.Pow(a.Item0 - b.Item0, 2) + Math.Pow(a.Item1 - b.Item1, 2) + Math.Pow(a.Item2 - b.Item2, 2));
            if (normals.Max(n => Distance(n, normals[0])) < .2) throw new InvalidDataException("Insufficient board tilt. Capture oblique views in both directions.");
            using var identity = Mat.Eye(3, 3, MatType.CV_64FC1).ToMat();
            var errors = new List<double>();
            foreach (var observed in validation)
            {
                using var normalized = new Mat();
                Cv2.FishEye.UndistortPoints(observed, normalized, matrix, distortion);
                using var rotation = new Mat();
                using var translation = new Mat();
                Cv2.SolvePnP(objects, normalized, identity, default, rotation, translation);
                if (rotation.Empty() || translation.Empty()) throw new InvalidDataException("Held-out pose estimation failed.");
                using var projected = new Mat();
                Cv2.FishEye.ProjectPoints(objects, projected, rotation, translation, matrix, distortion);
                errors.Add(Cv2.Norm(observed, projected, NormTypes.L2) / Math.Sqrt(count));
            }
            double[] k = Enumerable.Range(0, 4).Select(i => distortion.At<double>(i)).ToArray();
            double fx = matrix.At<double>(0, 0), fy = matrix.At<double>(1, 1), cx = matrix.At<double>(0, 2), cy = matrix.At<double>(1, 2);
            if (!new[] { rms, fx, fy, cx, cy }.Concat(k).Concat(errors).All(double.IsFinite) || fx <= 0 || fy <= 0 || rms > 1 || errors.Max() > 2)
                throw new InvalidDataException($"Lens fit rejected: training RMS {rms:F2}px, worst held-out RMS {errors.Max():F2}px. Recapture sharp, varied views.");
            double maxAngle = 0;
            foreach (var observed in points)
            {
                using var normalized = new Mat();
                Cv2.FishEye.UndistortPoints(observed, normalized, matrix, distortion);
                for (int i = 0; i < count; i++)
                {
                    var p = normalized.At<Vec2d>(0, i);
                    maxAngle = Math.Max(maxAngle, Math.Atan(Math.Sqrt(p.Item0 * p.Item0 + p.Item1 * p.Item1)));
                }
            }
            for (int i = 0; i <= 1000; i++)
            {
                double a = maxAngle * i / 1000, a2 = a * a;
                double derivative = 1 + 3 * k[0] * a2 + 5 * k[1] * a2 * a2 + 7 * k[2] * Math.Pow(a2, 3) + 9 * k[3] * Math.Pow(a2, 4);
                if (!double.IsFinite(derivative) || derivative <= 0) throw new InvalidDataException("Lens polynomial folds within the observed field of view.");
            }
            return new JsonObject {
                ["schema"] = 1, ["model"] = "kannala-brandt", ["device"] = meta["device"]!.GetValue<string>(), ["width"] = size.Width, ["height"] = size.Height,
                ["fx"] = fx, ["fy"] = fy, ["cx"] = cx, ["cy"] = cy, ["coefficients"] = new JsonArray(k.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                ["trainingRmsPixels"] = rms, ["validationRmsPixels"] = errors.Average(), ["validationWorstRmsPixels"] = errors.Max(),
                ["source"] = "checkerboard-held-out-validation", ["mountVerified"] = false,
                ["images"] = new JsonArray(names.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                ["validatedImageCoverage"] = new JsonArray(extent.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()) };
        }
        finally { foreach (var p in points) p.Dispose(); }
    }
}

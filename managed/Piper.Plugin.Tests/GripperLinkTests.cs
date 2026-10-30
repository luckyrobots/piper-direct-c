using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Plugin;

internal static class GripperLinkTests
{
    public static void Run(Action<bool, string> check)
    {
        JsonArray Model(double travel = .05, bool reversed = false)
        {
            var model = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["name"] = "joint" + i, ["unit"] = "rad" }).ToArray());
            model.Add(new JsonObject { ["name"] = "joint7", ["unit"] = "m", ["limited"] = true, ["min"] = reversed ? 0 : -travel, ["max"] = reversed ? travel : 0 });
            model.Add(new JsonObject { ["name"] = "joint8", ["unit"] = "m", ["limited"] = true, ["min"] = reversed ? -travel : 0, ["max"] = reversed ? 0 : travel });
            return model;
        }
        JsonObject Pika(double width, bool fresh = true) => new() { ["fresh"] = fresh,
            ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Position"] = PikaProtocol.Angle(width) } } };
        var device = new JsonObject { ["id"] = "arm-a", ["generation"] = 1L, ["fresh"] = true,
            ["joints"] = new JsonArray(Enumerable.Range(0, 6).Select(_ => (JsonNode)new JsonObject { ["actual_deg"] = 0.0 }).ToArray()) };
        foreach (bool reversed in new[] { false, true })
        {
            var link = new RobotLink("scene-a", "robot-a", Model(reversed: reversed), device);
            foreach (double width in new[] { 0.0, 45.0, 90.0, 45.0, 0.0 })
            {
                var request = link.Feedback(device, Pika(width), true);
                double first = request["joints"]!["joint7"]!.GetValue<double>();
                double second = request["joints"]!["joint8"]!.GetValue<double>();
                check(Math.Abs(first - (reversed ? 1 : -1) * (.05 - width / 2000)) < 1e-10 && Math.Abs(first + second) < 1e-12,
                    "Pika opening must decrease closing travel from the signed endpoint toward q=0");
                var pose = new JsonArray(request["joints"]!.AsObject().Select(j => (JsonNode)new JsonObject { ["name"] = j.Key, ["position"] = j.Value!.DeepClone() }).ToArray());
                link.FeedbackError(request, new JsonObject { ["pose"] = new JsonObject { ["joints"] = pose } });
                check(link.GripperErrorMm == 0, "Measured Pika finger acknowledgement was not verified separately in millimetres");
            }
            foreach (var sample in new (JsonObject? state, bool matches)[] { (null, true), (Pika(45, false), true), (Pika(45), false) })
            {
                var request = link.Feedback(device, sample.state, sample.matches);
                check(request["joints"]!.AsObject().Count == 6 && link.GripperStatus.Contains("unchanged", StringComparison.Ordinal),
                    "Stale/unassigned Pika feedback overwrote the last simulated opening");
                var pose = new JsonArray(request["joints"]!.AsObject().Select(j => (JsonNode)new JsonObject { ["name"] = j.Key, ["position"] = j.Value!.DeepClone() }).ToArray());
                link.FeedbackError(request, new JsonObject { ["pose"] = new JsonObject { ["joints"] = pose } });
                check(link.GripperErrorMm is null, "Stale Pika incorrectly required finger acknowledgements");
            }
        }
        var tooSmall = new RobotLink("scene-a", "small-tool", Model(.04), device);
        check(tooSmall.Feedback(device, Pika(90), true)["joints"]!.AsObject().Count == 6 && tooSmall.GripperStatus.Contains("80.0", StringComparison.Ordinal),
            "90 mm physical width was silently clamped into an 80 mm model");
        foreach (int invalid in Enumerable.Range(0, 5))
        {
            var model = Model();
            if (invalid == 0) model[6]!["unit"] = "rad";
            if (invalid == 1) model[7]!["name"] = "unknown-finger";
            if (invalid == 2) { model[7]!["min"] = -.05; model[7]!["max"] = 0; }
            if (invalid == 3) model[7]!["max"] = .04;
            if (invalid == 4) model[6]!["limited"] = false;
            var link = new RobotLink("scene-a", "unknown-tool", model, device);
            check(link.Feedback(device, Pika(45), true)["joints"]!.AsObject().Count == 6 && link.GripperStatus.Contains("not mapped", StringComparison.Ordinal),
                "Unknown gripper model was given invented finger mapping");
        }
        var invalidMotor = Pika(45); invalidMotor["telemetry"]!["motor"]!["Position"] = -1;
        check(new RobotLink("scene-a", "robot-a", Model(), device).Feedback(device, invalidMotor, true)["joints"]!.AsObject().Count == 6,
            "Invalid Pika encoder angle moved the simulated fingers");
        var noisyClosed = Pika(0);
        foreach (double noise in new[] { .007, .02, .09, .1236, .20, .249 })
        {
            noisyClosed["telemetry"]!["motor"]!["Position"] = -PikaProtocol.Angle(noise);
            var request = new RobotLink("scene-a", "robot-a", Model(), device).Feedback(device, noisyClosed, true);
            check(request["joints"]?["joint7"]?.GetValue<double>() == -.05 && request["joints"]?["joint8"]?.GetValue<double>() == .05,
                "Small negative closed-position feedback left the simulated gripper open");
        }
        noisyClosed["telemetry"]!["motor"]!["Position"] = -.0016;
        check(GripperLink.TryMeasuredOpening(noisyClosed, out double closed) && closed == 0,
            "Observed negative closed-stop encoder reading did not yield a zero opening for both scene and UI");
        check(!GripperLink.TryMeasuredOpening(Pika(45, false), out _), "Stale Pika opening was presented as a live measurement");
        noisyClosed["telemetry"]!["motor"]!["Position"] = -PikaProtocol.Angle(.3);
        check(new RobotLink("scene-a", "robot-a", Model(), device).Feedback(device, noisyClosed, true)["joints"]!.AsObject().Count == 6,
            "Negative Pika opening beyond the closed-noise allowance was silently clamped");

        // Audited PiperPod visual mesh: q=0 leaves a 94.044382 mm inner-jaw
        // gap; calibrated signed endpoints close that gap without overlap.
        const double openGapMm = 94.04438247823715;
        var calibrated = new RobotLink("scene-a", "mesh-audited-tool", Model(openGapMm / 2000), device);
        foreach (double width in new[] { 0.0, 20.0, 45.0, 90.0 })
        {
            var request = calibrated.Feedback(device, Pika(width), true);
            double left = request["joints"]!["joint7"]!.GetValue<double>();
            double right = request["joints"]!["joint8"]!.GetValue<double>();
            double visualGapMm = openGapMm + (left - right) * 1000;
            check(Math.Abs(visualGapMm - width) < 1e-7 && left <= 0 && right >= 0,
                "Audited Pika geometry did not reproduce the measured inner-jaw opening");
        }
    }
}

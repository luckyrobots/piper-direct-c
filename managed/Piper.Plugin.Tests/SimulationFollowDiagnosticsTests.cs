using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class SimulationFollowDiagnosticsTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var first = SimulationFollowSample.Read(SimulationFollowTests.Pose());
        double[] target = [-4.722, 116.529, -43.67, -.38, -57.95, 0];
        double[] actual = target.ToArray(); actual[1] = 116.52;
        var limits = Enumerable.Range(1, 6).Select(j => new EpisodeReplayJointLimit(j, -180, 180, 30, 20, true)).ToArray();
        var hardware = new SimulationFollowHardware("can-a", 7, true, true, true, actual, limits, 38.13,
            new("can-a", 7, 77, 12, actual.ToArray()));
        var execution = new OrderedSimulationExecution(first, hardware, 1);
        OrderedSimulationFrame Frame(ulong cursor, double time, string phase, double command, double measured, bool final = false) =>
            new(cursor, (long)(time * 1e9), time, target, target, command, measured, "physical-episode", 3, phase, final, final);
        execution.Path.Append([Frame(1, 1, "gripper:22", 80, 80), Frame(2, 1.1, "gripper:22", 36, 41.4),
            Frame(3, 1.2, "wait:23", 36, 39.2, true)]);
        execution.Step(0, hardware, false, 0, 1);
        try
        {
            execution.Step(execution.ActivePhase!.DurationSeconds + 15.01, hardware, false, 0, 2);
            check(false, "Diagnostic fixture did not reach the unchanged ordered endpoint timeout");
        }
        catch (InvalidOperationException) { }
        var ordered = SimulationFollowFailureOrdered.Capture(execution, hardware, false, 0, 2);
        actual[1] = 0;
        check(ordered.Kind == "Gripper" && ordered.Phase == "gripper:22" && ordered.EndCursor == 2 &&
            ordered.EndpointJointDegrees[1] == 116.529 && ordered.ActualJointDegrees[1] == 116.52 &&
            Math.Abs(ordered.EndpointJointErrorsDegrees[1] - .009) < 1e-9,
            "Queued diagnostics copied the completed leader home pose or retained mutable hardware arrays");
        check(ordered.ExpectedOpeningMm == 41.4 && ordered.ActualOpeningMm == 38.13 && ordered.EndpointCommandOpeningMm == 36 &&
            ordered.OpeningToleranceMm == 5 && ordered.RequiresPhysicalContactCandidate && ordered.ActualPhysicalContactCandidate && !ordered.ArmStationary,
            "Queued diagnostics omitted the actual contact window, candidate and stationary arrival requirements");
        string description = ordered.Describe();
        check(description.Contains("Pika 38.1 mm; expected 41.4 ±5.0 mm (command 36.0 mm)", StringComparison.Ordinal) &&
            description.Contains("J2 0.01°", StringComparison.Ordinal) && description.Contains("arm stationary no", StringComparison.Ordinal),
            "Ordered timeout details did not explain the actual jaw/arm discrepancy in readable units");

        string directory = Path.Combine(Path.GetTempPath(), "piper-ordered-diagnostics-" + Guid.NewGuid().ToString("N"));
        string reported = "";
        var diagnostics = new SimulationFollowDiagnostics(directory, "test", message => { reported = message; return Task.CompletedTask; });
        try
        {
            var failure = new SimulationFollowFailure(DateTimeOffset.UtcNow, "following-play", "ordered-phase-endpoint",
                nameof(InvalidOperationException), "The real arm or Pika did not settle.", 77, 12,
                new("source", "run", "robot", "model", "revision", "can-a", 7, "pika-a", 1),
                new("play", 99, 1, 1, 1, new double[6], new double[6], 80, 80, 11.82), null, Ordered: ordered);
            check(diagnostics.Capture(failure) && !diagnostics.Capture(failure with { Reason = "Later cleanup" }),
                "Queued diagnostics replaced the immutable original failure with cleanup");
            await diagnostics.Completion;
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(diagnostics.Pathname))!["firstFailure"]!;
            check(saved["Ordered"]!["ActualJointDegrees"]![1]!.GetValue<double>() == 116.52 &&
                saved["Runtime"]!["CommandDegrees"]![1]!.GetValue<double>() == 0 &&
                saved["Reason"]!.GetValue<string>().Contains(description, StringComparison.Ordinal) && reported.Contains(description, StringComparison.Ordinal),
                "The durable report or editor summary lost the physical queued phase behind the leader's completed home pose");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}

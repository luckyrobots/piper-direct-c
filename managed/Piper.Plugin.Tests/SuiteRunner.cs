using System.Reflection;
using System.Runtime.ExceptionServices;

/// <summary>One discovered suite entry point: a static, parameterless,
/// non-private <c>Run*</c> method of a <c>*Tests</c> class.</summary>
internal sealed record Suite(string Name, MethodInfo Method);

/// <summary>Runs every suite of this assembly. Suites are discovered, never
/// listed, so a new suite cannot be left out of the default run; the named
/// flags only narrow a local run.</summary>
internal static class SuiteRunner
{
    private static readonly List<string> skipped = [];
    private static string current = "";

    // Local filters. Each names suites by "Class." or "Class.Method" prefix.
    private static readonly Dictionary<string, string[]> filters = new(StringComparer.Ordinal)
    {
        ["--teaching-resume"] = ["TeachingResumeTests.", "SimulationFollowingTests.RunTeachingResume"],
        ["--primary-ui"] = ["SimulationFollowingTests.RunPrimaryWorkspaceAction"],
        ["--unified-follow"] = ["SimulationFollowingTests.RunUnifiedFollowing"],
        ["--group-follow"] = ["GroupSimulationFollowingTests.", "SimulationFollowingTests.RunGroupFollow", "SimulationFollowingTests.RunPrimaryWorkspaceAction"],
        ["--endpoint-stationary"] = ["SimulationEndpointStationaryTests.", "OrderedSimulationExecutionTests.", "SimulationFollowingTests.RunGroupFollow", "SimulationFollowingTests.RunUnifiedFollowing"],
        ["--telemetry-connection"] = ["TelemetryConnectionPresentationTests."],
        ["--recovery-ui"] = ["RecoveryWorkspaceTests."],
    };

    /// <summary>A suite that cannot run here says so; the run prints and counts it.</summary>
    public static void Skip(string reason) => skipped.Add(current + ": " + reason);

    public static IReadOnlyList<Suite> Discover(Assembly assembly)
    {
        var suites = new List<Suite>();
        var problems = new List<string>();
        foreach (var type in assembly.GetTypes().Where(type => !type.IsNested && type.Name.EndsWith("Tests", StringComparison.Ordinal)).OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            var entries = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(method => method.Name.StartsWith("Run", StringComparison.Ordinal) && !method.IsPrivate && !method.IsSpecialName)
                .OrderBy(method => method.Name, StringComparer.Ordinal).ToArray();
            if (entries.Length == 0) problems.Add(type.Name + " has no Run entry point, so it would never run");
            foreach (var method in entries)
            {
                if (method.GetParameters().Length != 0 || method.IsGenericMethodDefinition || (method.ReturnType != typeof(void) && method.ReturnType != typeof(Task)))
                    problems.Add(type.Name + "." + method.Name + " must be parameterless and return void or Task (make a helper private)");
                else suites.Add(new(type.Name + "." + method.Name, method));
            }
        }
        if (problems.Count > 0) throw new InvalidOperationException("Test suites that the runner cannot run: " + string.Join("; ", problems) + ".");
        return suites;
    }

    public static async Task<int> Main(string[] args)
    {
        var suites = Discover(typeof(SuiteRunner).Assembly);
        string? endpoint = null;
        string summary = "Pipers mapping, reconnect, limits and gRPC";
        if (args.Length == 1 && filters.TryGetValue(args[0], out var prefixes))
        {
            var chosen = suites.Where(suite => prefixes.Any(prefix => suite.Name.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
            var unmatched = prefixes.Where(prefix => !suites.Any(suite => suite.Name.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
            if (unmatched.Length > 0) throw new InvalidOperationException(args[0] + " names suites that no longer exist: " + string.Join(", ", unmatched));
            suites = chosen;
            summary = args[0][2..] + " (filtered)";
        }
        else if (args.Length == 1 && args[0] == SimulatedControllerChecks.Endpoint) endpoint = args[0];
        else if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: Piper.Plugin.Tests [" + string.Join(" | ", filters.Keys) + " | " + SimulatedControllerChecks.Endpoint + "]");
            return 2;
        }

        foreach (var suite in suites)
        {
            current = suite.Name;
            try
            {
                if (suite.Method.Invoke(null, null) is Task pending) await pending;
            }
            catch (Exception error)
            {
                var cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
                Console.Error.WriteLine("FAILED in " + suite.Name);
                ExceptionDispatchInfo.Capture(cause).Throw();
            }
        }
        current = "";
        if (endpoint is not null) await SimulatedControllerChecks.Verify(endpoint);

        foreach (string skip in skipped) Console.WriteLine("SKIPPED " + skip);
        string skips = skipped.Count == 0 ? "" : $" Skipped {skipped.Count} suite{(skipped.Count == 1 ? "" : "s")}.";
        Console.WriteLine($"Passed {Checks} {summary} checks in {suites.Count} suite{(suites.Count == 1 ? "" : "s")}.{skips}");
        return 0;
    }
}

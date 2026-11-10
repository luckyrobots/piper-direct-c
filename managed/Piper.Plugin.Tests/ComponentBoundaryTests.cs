using System.Runtime.CompilerServices;

/// <summary>P5: tests reach plugin components through their internal API. Private
/// state is private on purpose, so a test that reflects into it is a boundary
/// break, and this suite fails the build's test step when one appears.</summary>
internal static class ComponentBoundaryTests
{
    // The spelling is split so this file does not match itself.
    private const string Flag = "Non" + "Public";

    /// <summary>Every permitted use, each with the reason it is not a component boundary break.</summary>
    private static readonly Dictionary<string, int> Allowed = new()
    {
        ["Fixtures.cs"] = 1,            // the Lucky Engine SDK's PluginContext has no public constructor
        ["NativeAdmissionTests.cs"] = 1, // Piper.Core's ControllerService gate, outside the plugin's components
        ["SuiteRunner.cs"] = 1          // discovery of internal Run* entry points
    };

    private static string Here([CallerFilePath] string path = "") => path;

    public static void Run()
    {
        string here = Here();
        string? directory = Path.GetDirectoryName(here);
        if (directory is null || !File.Exists(Path.Combine(directory, "SuiteRunner.cs")))
        {
            Check(false, "The plugin test sources were not found beside " + here);
            return;
        }
        var found = Directory.GetFiles(directory, "*.cs")
            .Select(path => (Name: Path.GetFileName(path), Count: File.ReadAllText(path).Split(Flag).Length - 1))
            .Where(file => file.Count > 0).ToDictionary(file => file.Name, file => file.Count);
        foreach (var (name, count) in found)
            Check(Allowed.TryGetValue(name, out int allowed) && count == allowed,
                $"{name} uses {Flag} reflection {count} time(s); test plugin components through their internal API instead");
        Check(found.Count == Allowed.Count, "A permitted reflection use disappeared; remove it from the allow-list");
    }
}

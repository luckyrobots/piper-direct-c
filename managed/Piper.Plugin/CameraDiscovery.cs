using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

/// <summary>One read of the scene's cameras, stamped with the discovery epoch it was started under.</summary>
internal sealed record SimulationCameraDiscovery(long Epoch, JsonObject Cameras);
/// <summary>One read of the attached cameras; a null list could not be read this time.</summary>
internal sealed record CameraDeviceInventory(ColorCameraDevice[]? Color, DepthCameraDevice[]? Depth);

/// <summary>The Cal-F part of the Calibration component: what camera
/// discovery last found, in the scene and on the machine.
/// <code>
///   field             written by                                   readers see
///   simulationCameras the UI actor (ApplySimulation)               one volatile reference to a JSON
///                                                                  object nobody changes afterwards
///   epoch             the UI actor (Supersede)                     interlocked read
///   colorChoices,     the UI actor (ApplyInventory)                one volatile reference each to an
///   depthChoices                                                   array nobody changes afterwards
///   status            the UI actor (ApplyInventory) and the        one volatile reference to a string
///                     device probe (Report)
///   SimulationProbe,  their own transitions (ThrottledProbe):      at most one pending result each
///   DeviceProbe       queued and taken by the UI actor
///   shutdown          plugin disposal (Retire), once               a cancellation token
/// </code>
/// Not owned here: the saved camera bindings (preferences), the scene
/// inventory a discovery may carry (telemetry), and the camera workers (Cal-G).</summary>
internal sealed class CameraDiscovery : IDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private JsonObject? simulationCameras;
    private long epoch;
    private CameraDeviceChoice[] colorChoices = [], depthChoices = [];
    private string status = "Camera inventory is loading…";

    public CameraDiscovery() => Shutdown = shutdown.Token;

    public ThrottledProbe<SimulationCameraDiscovery> SimulationProbe { get; } = new(TimeSpan.FromSeconds(2));
    public ThrottledProbe<CameraDeviceInventory> DeviceProbe { get; } = new(TimeSpan.FromSeconds(15));

    /// <summary>Cancelled when discovery is retired.</summary>
    public CancellationToken Shutdown { get; }
    public bool Retired => shutdown.IsCancellationRequested;
    public JsonObject? SimulationCameras => Volatile.Read(ref simulationCameras);
    /// <summary>Advances when an explicit refresh supersedes periodic reads.</summary>
    public long Epoch => Interlocked.Read(ref epoch);
    public CameraDeviceChoice[] ColorChoices => Volatile.Read(ref colorChoices);
    public CameraDeviceChoice[] DepthChoices => Volatile.Read(ref depthChoices);
    public string Status => Volatile.Read(ref status);

    /// <summary>An explicit refresh: a periodic read still in flight, or pending, no longer applies.</summary>
    public void Supersede()
    {
        Interlocked.Increment(ref epoch);
        SimulationProbe.Take();
    }

    public void ApplySimulation(JsonObject discovered) => Volatile.Write(ref simulationCameras, discovered);

    /// <summary>Merges one device read into the choices. True when a choice list changed.</summary>
    public bool ApplyInventory(CameraDeviceInventory inventory)
    {
        string Lists() => JsonSerializer.Serialize(ColorChoices, PluginProtocol.Json) + JsonSerializer.Serialize(DepthChoices, PluginProtocol.Json);
        string previous = Lists();
        if (inventory.Color is { } color) Volatile.Write(ref colorChoices, CameraDeviceChoices.Merge(ColorChoices, color));
        if (inventory.Depth is { } depth)
            Volatile.Write(ref depthChoices, CameraDeviceChoices.Merge(DepthChoices,
                depth.Select(device => new ColorCameraDevice(device.Name + " · " + device.Serial, device.Serial)).ToArray()));
        Report($"Video inventory: {(inventory.Color is null ? "temporarily unavailable" : inventory.Color.Length)} · D405 inventory: {(inventory.Depth is null ? "temporarily unavailable" : inventory.Depth.Length)}");
        return Lists() != previous;
    }

    public void Report(string text) => Volatile.Write(ref status, text);

    /// <summary>Stops both probes for shutdown and drops any unapplied result.</summary>
    public async Task Retire()
    {
        await shutdown.CancelAsync();
        await Task.WhenAll(SimulationProbe.Retire(), DeviceProbe.Retire());
        shutdown.Dispose();
    }

    public void Dispose() => shutdown.Dispose();
}

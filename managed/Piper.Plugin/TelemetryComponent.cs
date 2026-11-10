using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>The Telemetry component (P5): everything the plugin knows because the
/// controller backend told it. It owns the fields below; nothing outside this
/// class can assign them, and every reader sees an already-published immutable
/// value without taking a lock, waiting on a queue or entering the command gate.
/// STOP reads <see cref="State"/> and nothing else here.
/// <code>
///   field          written by                                    readers see
///   receipts       telemetry reader loop (BeginStream, Publish,  one volatile reference to an
///                  Disconnect)                                   immutable TelemetryReceipt
///   backendToken   telemetry reader loop (AdoptBackend)          volatile string
///   scope          telemetry reader loop (AdoptScope)            volatile reference to an immutable record
///   state          telemetry reader loop (PublishState)          volatile reference; the JSON object is
///                                                                never mutated after publication
///   issue          telemetry reader loop (ReportIssue)           volatile string
/// </code>
/// The reader loop is <c>PipersPlugin.ReadTelemetry</c>. For each frame it adopts
/// the backend and scope (after the shell has discarded what the old scope
/// granted), then publishes the frame's state, then its receipt. Tests drive the
/// same methods from their one thread.</summary>
internal sealed class TelemetryComponent
{
    private readonly LatestTelemetry receipts = new();
    private volatile string backendToken = "";
    private volatile TelemetryScope? scope;
    private volatile JsonObject? state;
    private volatile string issue = "Waiting for controller feedback";

    /// <summary>The newest accepted frame with its receipt clock; null before the first.</summary>
    public TelemetryReceipt? Latest => receipts.Latest;
    /// <summary>The backend instance whose frames are current.</summary>
    public string BackendToken => backendToken;
    /// <summary>The active profile's assigned CAN connection under <see cref="BackendToken"/>.</summary>
    public TelemetryScope? Scope => scope;
    /// <summary>The newest published controller snapshot.</summary>
    public JsonObject? State => state;
    /// <summary>Why no frame is being accepted; empty while frames arrive.</summary>
    public string Issue => issue;

    /// <summary>A connected receipt from the current backend inside the freshness window.</summary>
    public bool IsFresh(long nowTicks) => Latest is { } receipt && receipt.BackendToken == backendToken && receipt.IsFresh(nowTicks);

    public void BeginStream() => receipts.BeginStream();

    public bool TryPrepare(JsonObject next, long sequence, long sourceUnixMicroseconds, string clockDomain,
        long receivedUnixMicroseconds, long receivedTicks, out TelemetryReceipt? receipt, out string reason) =>
        receipts.TryPrepare(next, sequence, sourceUnixMicroseconds, clockDomain, receivedUnixMicroseconds, receivedTicks, out receipt, out reason);

    public void AdoptBackend(string token) => backendToken = token;
    public void AdoptScope(TelemetryScope? next) => scope = next;
    public void PublishState(JsonObject next) => state = next;
    // The caller invalidates old command scope before publishing a new scope.
    public void Publish(TelemetryReceipt receipt) => receipts.Publish(receipt);
    /// <summary>Keeps the last receipt for inspection but stops it granting anything.</summary>
    public void Disconnect() => receipts.Disconnect();
    public void ReportIssue(string text) => issue = text;
}

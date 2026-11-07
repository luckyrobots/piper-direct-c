using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Pika;

public enum PikaHoldOutcome { Unverified, Skipped, Acknowledged }

/// <summary>Acknowledged includes fresh evidence that the gripper was already disabled.</summary>
public readonly record struct PikaHoldResult(PikaHoldOutcome Outcome, string? Message = null);

/// <summary>One physical gripper's transport, ownership and power evidence.</summary>
public interface IPikaSession : IAsyncDisposable
{
    JsonObject Snapshot(bool includeRawEvidence = false);

    /// <summary>Attempt a hold for this owner, skipping unrelated sessions and reporting unresolved power as unverified.</summary>
    Task<PikaHoldResult> HoldForOwnerAsync(string owner, CancellationToken cancellation);
    Task<JsonObject> ExecuteAsync(string action, JsonElement input, CancellationToken cancellation);
}

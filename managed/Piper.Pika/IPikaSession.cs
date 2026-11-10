using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Pika;

public enum PikaHoldOutcome { Unverified, Skipped, Acknowledged }

/// <summary>Acknowledged includes fresh evidence that the gripper was already disabled.</summary>
public readonly record struct PikaHoldResult(PikaHoldOutcome Outcome, string? Message = null);

/// <summary>The few session facts routing needs, without cloning a snapshot.</summary>
public readonly record struct PikaSessionStatus(bool Connected, string? Owner, bool ConfigurationBlocked)
{
    public static PikaSessionStatus From(JsonObject snapshot) => new(snapshot["connected"]?.GetValue<bool>() == true,
        snapshot["owner"]?.GetValue<string>(), snapshot["configuration_blocked"]?.GetValue<bool>() == true);
}

/// <summary>One physical gripper's transport, ownership and power evidence.</summary>
public interface IPikaSession : IAsyncDisposable
{
    JsonObject Snapshot(bool includeRawEvidence = false);

    /// <summary>Cheap routing status. Sessions without a cheaper source derive it from <see cref="Snapshot"/>.</summary>
    PikaSessionStatus Status => PikaSessionStatus.From(Snapshot());

    /// <summary>Attempt a hold for this owner, skipping unrelated sessions and reporting unresolved power as unverified.</summary>
    Task<PikaHoldResult> HoldForOwnerAsync(string owner, CancellationToken cancellation);
    Task<JsonObject> ExecuteAsync(string action, JsonElement input, CancellationToken cancellation);
}

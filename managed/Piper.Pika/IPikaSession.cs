using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Pika;

/// <summary>One physical gripper's transport, ownership and power evidence.</summary>
public interface IPikaSession : IAsyncDisposable
{
    JsonObject Snapshot(bool includeRawEvidence = false);
    Task<JsonObject> ExecuteAsync(string action, JsonElement input, CancellationToken cancellation);
}

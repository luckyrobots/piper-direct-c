using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public static class BackendGuards
{
    public static bool IsOwned(JsonNode snapshot, int processId, string? sessionToken) =>
        snapshot["backend_process_id"]?.GetValue<int>() == processId &&
        !string.IsNullOrEmpty(snapshot["token"]?.GetValue<string>()) &&
        (sessionToken is null || snapshot["token"]?.GetValue<string>() == sessionToken);

    public static async Task<bool> PortOccupied(int port, CancellationToken cancellation)
    {
        using var client = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
        try { await client.ConnectAsync("127.0.0.1", port, deadline.Token); return true; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return false; }
    }
}

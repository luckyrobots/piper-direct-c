using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Net.Client;
using LuckyRobots.Piper.Grpc;

/// <summary>Optional checks against a separately started simulated controller.
/// Not a suite: it needs the endpoint argument and a running server.</summary>
internal static class SimulatedControllerChecks
{
    public const string Endpoint = "http://127.0.0.1:18766";

    public static async Task Verify(string endpoint)
    {
        // This optional integration suite is restricted to the separate simulated server.
        if (endpoint != "http://127.0.0.1:18766") throw new ArgumentException("Use the isolated simulation endpoint on 18766.");
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new PiperControl.PiperControlClient(channel);
        var frame = await client.GetStateAsync(new(), deadline: DateTime.UtcNow.AddSeconds(5));
        var snapshot = JsonNode.Parse(frame.Json)!.AsObject();
        Check(frame.Sequence > 0 && frame.ClockDomain == "host-utc", "Snapshot metadata missing");
        string token = snapshot["token"]!.GetValue<string>();
        await RejectAsync<RpcException>(async () => await client.ExecuteAsync(new() { Route = "/api/query", Json = "{}" }),
            "Unauthenticated command accepted", e => e.StatusCode == StatusCode.Unauthenticated);
        await RejectAsync<RpcException>(async () => await client.ExecuteAsync(new() { Route = "/api/query", Json = "{\"a\":1,\"a\":2}" }, new Metadata { { "x-piper-token", token } }),
            "Duplicate command keys accepted", e => e.StatusCode == StatusCode.InvalidArgument);
        var pika = await client.ExecuteAsync(new() { Route = "/api/pika/connect", Json = "{}" }, new Metadata { { "x-piper-token", token } });
        Check(pika.Status == 409, "Simulation opened real Pika hardware");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var stream = client.WatchState(new(), cancellationToken: cancellation.Token);
        long previous = 0;
        for (int i = 0; i < 5; i++)
        {
            Check(await stream.ResponseStream.MoveNext(cancellation.Token), "State stream ended");
            Check(stream.ResponseStream.Current.Sequence > previous, "Non-monotonic stream sequence");
            previous = stream.ResponseStream.Current.Sequence;
        }
    }
}

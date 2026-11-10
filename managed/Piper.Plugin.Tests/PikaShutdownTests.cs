using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

internal static class PikaShutdownTests
{
    public static async Task Run()
    {
        const string backend = "shutdown-fixture";
        const string linux = "/dev/pika-linux", stalled = "/dev/pika-stalled", monitor = "/dev/pika-monitor",
            transferred = "/dev/pika-transferred", foreign = "/dev/pika-foreign";
        var plugin = new PipersPlugin();
        string owner = plugin.owner;
        string otherOwner = Guid.NewGuid().ToString();
        var telemetry = plugin.telemetry;
        plugin.telemetry.AdoptBackend(backend);
        var requests = new ConcurrentQueue<(string Route, string Device, string? Distribution, string Owner)>();
        var linuxDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new Calls(async (command, cancellation) =>
        {
            using var json = JsonDocument.Parse(command.Json);
            var input = json.RootElement;
            string device = input.GetProperty("pika_device").GetString()!;
            string? distribution = input.GetProperty("pika_distribution").GetString();
            requests.Enqueue((command.Route, device, distribution, input.GetProperty("owner").GetString()!));
            if (device == stalled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            if (command.Route == "/api/pika/disconnect" && device == linux) linuxDisconnected.TrySetResult();
            return new() { Status = 200, Json = command.Route == "/api/pika/disable"
                ? JsonSerializer.Serialize(new { ok = true, motor_request_id = device + "-shutdown" })
                : "{\"ok\":true}" };
        });
        plugin.client = new PiperControl.PiperControlClient(calls);
        var grippers = new[]
        {
            Gripper(linux, null, owner, powered: true), Gripper(stalled, "PeerWsl", owner, powered: true),
            Gripper(monitor, null, owner, powered: false), Gripper(transferred, null, owner, powered: true),
            Gripper(foreign, null, otherOwner, powered: true)
        };
        void Publish(long sequence) => telemetry.Publish(new(new JsonObject
        {
            ["token"] = backend, ["pika"] = grippers[0].DeepClone(),
            ["pikas"] = new JsonArray(grippers.Select(p => p.DeepClone()).ToArray())
        }, backend, sequence, Stopwatch.GetTimestamp(), 0));
        Publish(1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var release = plugin.ReleaseOwnedPikas(deadline.Token);
        try
        {
            var admitted = requests.ToArray();
            Check(admitted.Count(r => r.Route == "/api/pika/disable") == 3 &&
                admitted.Any(r => r.Route == "/api/pika/disable" && r.Device == linux && r.Distribution is null) &&
                admitted.Any(r => r.Route == "/api/pika/disable" && r.Device == stalled && r.Distribution == "PeerWsl") &&
                admitted.Any(r => r.Route == "/api/pika/disconnect" && r.Device == monitor && r.Distribution is null),
                "A stalled device blocked independent shutdown requests or Linux null was routed through the active WSL profile");
            Check(!linuxDisconnected.Task.IsCompleted, "Cached disabled evidence from another motor request released the Linux gripper");
            grippers[0]["last_motor_request"] = linux + "-shutdown";
            grippers[3]["last_motor_request"] = transferred + "-shutdown";
            grippers[3]["owner"] = otherOwner;
            Publish(2);
            await linuxDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Check(!release.IsCompleted && requests.All(r => r.Owner == owner && r.Device != foreign) &&
                requests.Where(r => r.Route == "/api/pika/disconnect").Select(r => r.Device).Order().SequenceEqual(new[] { linux, monitor }),
                "Shutdown skipped a causally verified Linux disconnect or disconnected a foreign or unverified gripper");
        }
        finally
        {
            await deadline.CancelAsync();
            await release;
            plugin.channel.Dispose();
        }

        static JsonObject Gripper(string device, string? distribution, string owner, bool powered) => new()
        {
            ["device"] = device, ["distribution"] = distribution, ["owner"] = owner,
            ["configured"] = true, ["connected"] = true, ["fresh"] = true,
            ["enabled"] = false, ["armed"] = false, ["pending"] = false, ["following"] = false,
            ["resuming_hold"] = false, ["power_uncertain"] = false, ["power_requested"] = powered,
            ["last_motor_request"] = "previous-disable", ["age_s"] = .001, ["request_time_s"] = 1d, ["feedback_time_s"] = 2d
        };
    }

    private sealed class Calls(Func<ControlRequest, CancellationToken, Task<ControlReply>> execute) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options, TRequest request)
        {
            async Task<TResponse> Reply() => (TResponse)(object)await execute((ControlRequest)(object)request, options.CancellationToken);
            return new(Reply(), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}

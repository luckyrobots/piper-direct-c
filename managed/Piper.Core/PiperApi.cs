using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper;

/// <summary>What a route may do. STOP-class routes are never refused for ownership or
/// maintenance; maintenance routes end or restart the host itself.</summary>
public enum ApiRouteClass { Read, Control, Stop, Maintenance }

/// <summary>Which server implements a route: the C server, the managed host, or both.</summary>
public enum ApiBackend { C, Managed, Both }

/// <summary>One route of the API definition (managed/Piper.Contracts/piper-api.json).
/// <paramref name="SessionOwned"/>: a plugin-owned host admits the route only from its
/// session owner. <paramref name="Grpc"/>: the gRPC tunnel carries the route.
/// <paramref name="PikaAction"/>: the gripper action a /api/pika/ control names.</summary>
public sealed record ApiRoute(string Id, string Method, string Path, ApiRouteClass Class, ApiBackend Backend,
    bool SessionOwned, bool Grpc, string? PikaAction);

/// <summary>An optional request field whose value may itself be null: absent is not sent,
/// present null is sent as null.</summary>
public readonly record struct ApiOptional<T>(bool Present, T Value)
{
    public static implicit operator ApiOptional<T>(T value) => new(true, value);
}

/// <summary>A request built by <see cref="PiperApi.Requests"/>: the route and its JSON body.</summary>
public readonly record struct ApiRequest(ApiRoute Route, JsonObject Body)
{
    public string Json => Body.ToJsonString();
}

/// <summary>The API definition as code. The route table, paths, limits and request builders
/// are generated (PiperApi.g.cs); this part only looks routes up.</summary>
public static partial class PiperApi
{
    private static class Index
    {
        internal static readonly FrozenDictionary<string, ApiRoute> ManagedControls = Routes
            .Where(route => route.Method == "POST" && route.Backend != ApiBackend.C)
            .ToFrozenDictionary(route => route.Path, StringComparer.Ordinal);
    }

    /// <summary>The control (POST) route the managed host serves at this path, or null. A path
    /// that is not in the definition, or belongs to the C server only, is not a route here.</summary>
    public static ApiRoute? ManagedControl(string path) => Index.ManagedControls.GetValueOrDefault(path);

    /// <summary>True for the STOP class: admitted whoever asks and whatever else is frozen.</summary>
    public static bool IsStop(string path) => ManagedControl(path)?.Class == ApiRouteClass.Stop;
}

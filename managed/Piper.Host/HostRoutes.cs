namespace LuckyRobots.Piper.Host;

/// <summary>Routes the host itself interprets. Native routes are validated by the core.</summary>
internal static class HostRoutes
{
    internal const string State = "/api/state";
    internal const string OpenApi = "/api/openapi.json";
    internal const string Command = "/api/command";
    internal const string Select = "/api/select";
    internal const string Heartbeat = "/api/heartbeat";
    internal const string Query = "/api/query";
    internal const string Reconnect = "/api/reconnect";
    internal const string Stop = "/api/stop";
    internal const string CoordinatedStop = "/api/stop/coordinated";
    internal const string Shutdown = "/api/shutdown";
    internal const string Restart = "/api/maintenance/restart";
    internal const string PikaPrefix = "/api/pika/";
    internal const string PikaState = PikaPrefix + "state";
    internal const string PikaDevices = PikaPrefix + "devices";
    internal const string PikaConfigure = PikaPrefix + "configure";

    internal static bool IsPika(string route) => route.StartsWith(PikaPrefix, StringComparison.Ordinal);

    /// <summary>The registry action named by a /api/pika/ route.</summary>
    internal static string PikaAction(string route) => route[PikaPrefix.Length..];

    /// <summary>Emergency STOP stays admitted while maintenance freezes other controls.</summary>
    internal static bool IsEmergencyStop(string route) => route is Stop or CoordinatedStop;

    /// <summary>gRPC carries controls only; shutdown and unknown routes are HTTP-only.</summary>
    internal static bool IsGrpcControl(string route) =>
        route is Command or Select or Stop or Heartbeat or Query or Reconnect or CoordinatedStop or Restart || IsPika(route);
}

/// <summary>Loopback listener defaults and request limits shared by both transports.</summary>
internal static class HostEndpoints
{
    internal const int DefaultHttpPort = 8765;
    internal const int MinimumPort = 1024, MaximumPort = 65535;
    /// <summary>Kestrel body and gRPC receive limit; the control JSON limit is lower.</summary>
    internal const int MaxRequestBytes = 32768;
    internal const int MaxGrpcSendBytes = 1024 * 1024;

    internal static string[] LoopbackHosts(int port) => [$"127.0.0.1:{port}", $"localhost:{port}"];
}

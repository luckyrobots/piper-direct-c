namespace LuckyRobots.Piper.Host;

/// <summary>Loopback listener defaults and transport limits shared by both transports.
/// Routes and the control-body limits come from the API definition (<see cref="PiperApi"/>).</summary>
internal static class HostEndpoints
{
    internal const int DefaultHttpPort = 8765;
    internal const int MinimumPort = 1024, MaximumPort = 65535;
    /// <summary>Kestrel body and gRPC receive limit; the control JSON limit is lower.</summary>
    internal const int MaxRequestBytes = 32768;
    internal const int MaxGrpcSendBytes = 1024 * 1024;

    internal static string[] LoopbackHosts(int port) => [$"127.0.0.1:{port}", $"localhost:{port}"];
}

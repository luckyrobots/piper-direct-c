using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper;

/// <summary>Reusable controller owner for the web host or Lucky Engine.
/// All native calls are serialized. Construction only starts monitoring.</summary>
public sealed class ControllerService : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private IntPtr core;
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private volatile string? workerError;
    private int disposed;

    public ControllerService(bool simulated, string stateDirectory, int simulatedRobotCount = 1)
    {
        if (simulatedRobotCount is < 1 or > 16 || !simulated && simulatedRobotCount != 1)
            throw new ArgumentOutOfRangeException(nameof(simulatedRobotCount));
        Directory.CreateDirectory(stateDirectory);
        core = simulated ? Native.CreateSimulated(simulatedRobotCount, Path.GetFullPath(stateDirectory)) : Native.Create(0, Path.GetFullPath(stateDirectory));
        if (core == IntPtr.Zero) throw new InvalidOperationException("Cannot initialize controller. Inspect state files; never delete recovery state to bypass a stop.");
        worker = Task.Factory.StartNew(() =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    // Dispose joins this loop before it destroys the core.
                    lock (gate) { Debug.Assert(core != IntPtr.Zero); Native.Tick(core); }
                    cancellation.Token.WaitHandle.WaitOne(5);
                }
            }
            catch (Exception e) { workerError = e.Message; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public string Snapshot()
    {
        lock (gate) { Check(); return Take(Native.State(core, Token)); }
    }

    /// <summary>The native state parsed into a fresh, caller-owned object.</summary>
    public JsonObject SnapshotJson() => JsonNode.Parse(Snapshot())!.AsObject();

    // Only a prepared, synchronous routing commit belongs here. Discovery and
    // other transport waits must finish before taking the native action gate.
    internal T CommitWhenArmIdle<T>(Func<T> commit, string activeMessage, CancellationToken requestCancellation)
    {
        lock (gate)
        {
            requestCancellation.ThrowIfCancellationRequested();
            if (ControllerActivity.AnyMotion(SnapshotJson())) // re-enters gate; Check() runs there
                throw new InvalidOperationException(activeMessage);
            requestCancellation.ThrowIfCancellationRequested();
            return commit();
        }
    }

    public (int Status, string Json) Execute(string route, string json, CancellationToken requestCancellation = default)
    {
        lock (gate)
        {
            // Cancellation while waiting for a native tick/STOP must prevent
            // later admission. The synchronous action completes under this
            // same gate, so a subsequent hold is ordered after it.
            requestCancellation.ThrowIfCancellationRequested();
            Check();
            var result = Native.Action(core, route, json, out int status);
            return (status, result == IntPtr.Zero ? "{\"ok\":false,\"error\":\"Invalid controller request\"}" : Take(result));
        }
    }
    private void Check()
    {
        ObjectDisposedException.ThrowIf(core == IntPtr.Zero, this);
        if (workerError is { } failure) throw new InvalidOperationException($"Controller loop stopped: {failure}");
    }
    private static string Take(IntPtr value)
    {
        if (value == IntPtr.Zero) throw new OutOfMemoryException();
        try { return Marshal.PtrToStringUTF8(value) ?? throw new InvalidOperationException("Invalid native response"); }
        finally { Native.Release(value); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cancellation.Cancel(); worker.GetAwaiter().GetResult();
        lock (gate) { if (core != IntPtr.Zero) { Native.Destroy(core); core = IntPtr.Zero; } }
        cancellation.Dispose();
    }
    private static class Native
    {
        private const string Library = "piper-core";
        [DllImport(Library, EntryPoint="piper_create", CallingConvention=CallingConvention.Cdecl)]
        internal static extern IntPtr Create(int simulated, [MarshalAs(UnmanagedType.LPUTF8Str)] string directory);
        [DllImport(Library, EntryPoint="piper_create_simulated", CallingConvention=CallingConvention.Cdecl)]
        internal static extern IntPtr CreateSimulated(int count, [MarshalAs(UnmanagedType.LPUTF8Str)] string directory);
        [DllImport(Library, EntryPoint="piper_tick", CallingConvention=CallingConvention.Cdecl)] internal static extern void Tick(IntPtr core);
        [DllImport(Library, EntryPoint="piper_state", CallingConvention=CallingConvention.Cdecl)]
        internal static extern IntPtr State(IntPtr core, [MarshalAs(UnmanagedType.LPUTF8Str)] string token);
        [DllImport(Library, EntryPoint="piper_action", CallingConvention=CallingConvention.Cdecl)]
        internal static extern IntPtr Action(IntPtr core, [MarshalAs(UnmanagedType.LPUTF8Str)] string route, [MarshalAs(UnmanagedType.LPUTF8Str)] string body, out int status);
        [DllImport(Library, EntryPoint="piper_release", CallingConvention=CallingConvention.Cdecl)] internal static extern void Release(IntPtr json);
        [DllImport(Library, EntryPoint="piper_destroy", CallingConvention=CallingConvention.Cdecl)] internal static extern void Destroy(IntPtr core);
    }
}

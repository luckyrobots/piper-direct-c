using System.Runtime.InteropServices;
using System.Security.Cryptography;

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
    public string? WorkerError { get; private set; }

    public ControllerService(bool simulated, string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        core = Native.Create(simulated ? 1 : 0, Path.GetFullPath(stateDirectory));
        if (core == IntPtr.Zero) throw new InvalidOperationException("Cannot initialize controller. Inspect state files; never delete recovery state to bypass a stop.");
        worker = Task.Factory.StartNew(() =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    lock (gate) { if (core != IntPtr.Zero) Native.Tick(core); }
                    cancellation.Token.WaitHandle.WaitOne(5);
                }
            }
            catch (Exception e) { WorkerError = e.Message; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public string Snapshot()
    {
        lock (gate) { Check(); return Take(Native.State(core, Token)); }
    }
    public (int Status, string Json) Execute(string route, string json)
    {
        lock (gate)
        {
            Check();
            var result = Native.Action(core, route, json, out int status);
            return (status, result == IntPtr.Zero ? "{\"ok\":false,\"error\":\"Invalid controller request\"}" : Take(result));
        }
    }
    private void Check()
    {
        ObjectDisposedException.ThrowIf(core == IntPtr.Zero, this);
        if (WorkerError is not null) throw new InvalidOperationException($"Controller loop stopped: {WorkerError}");
    }
    private static string Take(IntPtr value)
    {
        if (value == IntPtr.Zero) throw new OutOfMemoryException();
        try { return Marshal.PtrToStringUTF8(value) ?? throw new InvalidOperationException("Invalid native response"); }
        finally { Native.Release(value); }
    }
    public void Dispose()
    {
        cancellation.Cancel(); worker.GetAwaiter().GetResult();
        lock (gate) { if (core != IntPtr.Zero) { Native.Destroy(core); core = IntPtr.Zero; } }
        cancellation.Dispose();
    }
    private static class Native
    {
        private const string Library = "piper-core";
        [DllImport(Library, EntryPoint="piper_create", CallingConvention=CallingConvention.Cdecl)]
        internal static extern IntPtr Create(int simulated, [MarshalAs(UnmanagedType.LPUTF8Str)] string directory);
        [DllImport(Library, EntryPoint="piper_tick", CallingConvention=CallingConvention.Cdecl)] internal static extern void Tick(IntPtr core);
        [DllImport(Library, EntryPoint="piper_state", CallingConvention=CallingConvention.Cdecl)]
        internal static extern IntPtr State(IntPtr core, [MarshalAs(UnmanagedType.LPUTF8Str)] string token);
        [DllImport(Library, EntryPoint="piper_action", CallingConvention=CallingConvention.Cdecl)]
        internal static extern IntPtr Action(IntPtr core, [MarshalAs(UnmanagedType.LPUTF8Str)] string route, [MarshalAs(UnmanagedType.LPUTF8Str)] string body, out int status);
        [DllImport(Library, EntryPoint="piper_release", CallingConvention=CallingConvention.Cdecl)] internal static extern void Release(IntPtr json);
        [DllImport(Library, EntryPoint="piper_destroy", CallingConvention=CallingConvention.Cdecl)] internal static extern void Destroy(IntPtr core);
    }
}

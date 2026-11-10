using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using LuckyRobots.Piper;

internal static class NativeAdmissionTests
{
    private static IntPtr nativeLibrary;

    // Runs against the real native library, always with simulated:true; this
    // fixture never opens CAN or serial. Without PIPER_TEST_NATIVE_PATH the suite
    // is reported as skipped, and PIPER_TEST_REQUIRE_NATIVE=1 (CI) makes that a failure.
    public static async Task Run()
    {
        string? configured = Environment.GetEnvironmentVariable("PIPER_TEST_NATIVE_PATH");
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (Environment.GetEnvironmentVariable("PIPER_TEST_REQUIRE_NATIVE") == "1")
                throw new InvalidOperationException("PIPER_TEST_REQUIRE_NATIVE=1 but PIPER_TEST_NATIVE_PATH does not name the native library; the native admission checks did not run.");
            SuiteRunner.Skip("PIPER_TEST_NATIVE_PATH is not set, so the native admission checks did not run");
            return;
        }
        string library = Path.GetFullPath(configured);
        if (!Path.IsPathFullyQualified(configured) || !File.Exists(library))
            throw new InvalidOperationException("PIPER_TEST_NATIVE_PATH must name an existing absolute native library path.");
        if (nativeLibrary == IntPtr.Zero)
        {
            nativeLibrary = NativeLibrary.Load(library);
            NativeLibrary.SetDllImportResolver(typeof(ControllerService).Assembly,
                (name, _, _) => name == "piper-core" ? nativeLibrary : IntPtr.Zero);
        }

        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        string directory = Path.Combine(temporaryRoot, "piper-native-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var controller = new ControllerService(simulated: true, directory);
            var initial = JsonNode.Parse(controller.Snapshot())!.AsObject();
            Check(initial["simulated"]?.GetValue<bool>() == true, "Native admission fixture did not use the hardware-disabled simulator");
            string device = initial["selected"]!.GetValue<string>();
            long generation = initial["devices"]!.AsArray().OfType<JsonObject>().Single(d => d["id"]!.GetValue<string>() == device)["generation"]!.GetValue<long>();
            string query = new JsonObject { ["device_id"] = device, ["generation"] = generation }.ToJsonString();
            string select = new JsonObject { ["device_id"] = device }.ToJsonString();
            object gate = typeof(ControllerService).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;

            // Prove this exact uncancelled request would mutate query state,
            // while excluding the native worker from completing it first.
            lock (gate)
            {
                var accepted = controller.Execute("/api/query", query);
                Check(accepted.Status == 202 && JsonNode.Parse(controller.Snapshot())?["query_active"]?.GetValue<bool>() == true,
                    "Native fixture query was not a valid mutating admission");
                controller.Execute("/api/stop", "{}");
            }

            await CancelAtGate("/api/query", query, stopFirst: false);
            await CancelAtGate("/api/query", query, stopFirst: true);
            await CancelAtGate("/api/select", select, stopFirst: true);

            async Task CancelAtGate(string route, string body, bool stopFirst)
            {
                using var releaseGate = new ManualResetEventSlim();
                using var requestStarted = new ManualResetEventSlim();
                using var cancelled = new CancellationTokenSource();
                var holderReady = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                var holderDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var holder = new Thread(() =>
                {
                    try
                    {
                        lock (gate)
                        {
                            holderReady.TrySetResult(JsonNode.Parse(controller.Snapshot())!.AsObject());
                            releaseGate.Wait();
                            if (stopFirst) controller.Execute("/api/stop", "{}");
                        }
                        holderDone.TrySetResult();
                    }
                    catch (Exception error) { holderReady.TrySetException(error); holderDone.TrySetException(error); }
                }) { IsBackground = true };
                Thread? requester = null;
                holder.Start();
                try
                {
                    JsonObject before = await holderReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    requester = new Thread(() =>
                    {
                        try
                        {
                            requestStarted.Set();
                            controller.Execute(route, body, cancelled.Token);
                            result.TrySetResult(false);
                        }
                        catch (OperationCanceledException) { result.TrySetResult(true); }
                        catch (Exception error) { result.TrySetException(error); }
                    }) { IsBackground = true };
                    requester.Start();
                    if (!requestStarted.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Native admission thread did not start");
                    long began = Stopwatch.GetTimestamp();
                    // Deterministically cancel after the caller is waiting on
                    // Monitor, rather than merely cancelling before Execute.
                    while ((requester.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
                    {
                        if (Stopwatch.GetElapsedTime(began).TotalSeconds > 2) throw new InvalidOperationException("Native request never blocked at its admission lock");
                        await Task.Delay(1);
                    }
                    cancelled.Cancel();
                    releaseGate.Set();
                    Check(await result.Task.WaitAsync(TimeSpan.FromSeconds(3)), "A cancelled queued native " + route + " request was admitted after the lock became available");
                    await holderDone.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    var after = JsonNode.Parse(controller.Snapshot())!.AsObject();
                    Check(after["selected"]!.GetValue<string>() == before["selected"]!.GetValue<string>() && after["query_active"]?.GetValue<bool>() == false,
                        "A cancelled queued native request mutated adapter selection or started a later query");
                    if (stopFirst)
                        Check(after["phase"]?.GetValue<string>() == "stopped" && after["message"]!.GetValue<string>().StartsWith("Operator global stop.", StringComparison.Ordinal),
                            "An old queued request overwrote the native STOP after cancellation");
                }
                finally
                {
                    cancelled.Cancel(); releaseGate.Set();
                    holder.Join(TimeSpan.FromSeconds(3)); requester?.Join(TimeSpan.FromSeconds(3));
                }
            }
        }
        finally
        {
            string resolved = Path.GetFullPath(directory);
            if (Path.GetDirectoryName(resolved) != temporaryRoot || !Path.GetFileName(resolved).StartsWith("piper-native-admission-", StringComparison.Ordinal))
                throw new InvalidOperationException("Native fixture cleanup escaped its explicitly created temporary directory");
            Directory.Delete(resolved, recursive: true);
        }
    }
}

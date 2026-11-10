using System.Diagnostics;

namespace LuckyRobots.Piper.Pika;

/// <summary>Runs a short helper process (usbipd, wslpath, chmod) with closed
/// stdin and both output streams read, under a deadline that kills its whole
/// process tree. The process has always exited when this returns or throws.</summary>
internal static class BoundedProcess
{
    internal readonly record struct Result(int ExitCode, string Output, string Error);

    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<Result> RunAsync(string executable, IEnumerable<string> arguments, string startFailure,
        string timeoutMessage, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        using var process = Process.Start(start) ?? throw new IOException(startFailure);
        process.StandardInput.Close();
        using var stop = deadline.Token.Register(() => KillTree(process));
        Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token), errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await output, await errors);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException(timeoutMessage); }
        finally
        {
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
        }
    }

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* Already exited. */ }
    }
}

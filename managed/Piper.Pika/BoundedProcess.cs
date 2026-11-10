using System.Diagnostics;

namespace LuckyRobots.Piper.Pika;

/// <summary>Runs a short helper process (usbipd, wslpath, chmod) with closed
/// stdin and both output streams read, under a deadline that kills its whole
/// process tree. The process has exited when this returns or throws, unless
/// the operating system refused the kill; that wait is bounded too. It also
/// owns the two process primitives every Pika launch shares: a start whose
/// failure is a control error, and a tree kill that never throws.</summary>
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
        using var process = Start(start, startFailure);
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
            try { await Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), output, errors).WaitAsync(KillWait); }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException) { }
        }
    }

    /// <summary>How long a killed helper may take to disappear before the caller stops waiting.</summary>
    internal static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    /// <summary>Starts a process. A missing or non-executable binary is an
    /// <see cref="IOException"/> naming the operation, which both transports
    /// report as a control error instead of an unhandled server fault.</summary>
    internal static Process Start(ProcessStartInfo start, string startFailure)
    {
        try { return Process.Start(start) ?? throw new IOException(startFailure); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        { throw new IOException($"{startFailure} ({start.FileName}: {e.Message})", e); }
    }

    /// <summary>Kills a process tree. It runs on timer and cancellation
    /// callbacks, where an escaping exception would end the Host, so every
    /// failure the runtime documents is reported as <c>false</c> instead.</summary>
    internal static bool KillTree(Process process) => KillTree(process, static target => target.Kill(entireProcessTree: true));

    internal static bool KillTree(Process process, Action<Process> kill)
    {
        try { if (!process.HasExited) kill(process); return true; }
        // Already exited or never started; access denied or a vanished child
        // (Win32Exception, or AggregateException for a partly killed tree).
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException or NotSupportedException)
        { return false; }
    }
}

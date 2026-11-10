namespace LuckyRobots.Piper.Testing;

/// <summary>A failed check. The runners stop at the first one.</summary>
internal sealed class CheckFailedException(string message) : Exception(message);

/// <summary>The one assertion vocabulary of the custom managed test runners.
/// Each test project links this file and imports it statically, so every
/// runner counts the same thing: each evaluated <see cref="Check"/>, including
/// those made by <see cref="Reject{TException}"/>, counts once.</summary>
internal static class TestKit
{
    private static int checks;

    /// <summary>Checks evaluated so far in this process.</summary>
    public static int Checks => Volatile.Read(ref checks);

    public static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref checks);
        if (!condition) throw new CheckFailedException(message);
    }

    /// <summary>How long <see cref="Eventually"/> waits. It bounds a hung test and is
    /// never a timing assertion, so it is far longer than any loaded runner needs.</summary>
    public static readonly TimeSpan LivenessLimit = TimeSpan.FromSeconds(30);

    /// <summary>Waits for work on another thread to make <paramref name="condition"/> true.
    /// <paramref name="failure"/> is evaluated only on failure, so it can report the state it found.</summary>
    public static Task Eventually(Func<bool> condition, Func<string> failure) =>
        Eventually(() => Task.FromResult(condition()), failure);

    /// <summary>As above, for a condition that has to drive the code under test on each attempt.</summary>
    public static async Task Eventually(Func<Task<bool>> condition, Func<string> failure)
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!await condition())
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(began) > LivenessLimit) throw new TimeoutException(failure());
            await Task.Delay(5);
        }
    }

    public static Task Eventually(Func<bool> condition, string failure) => Eventually(condition, () => failure);

    /// <summary>Checks that <paramref name="action"/> is refused with an <see cref="InvalidOperationException"/>.</summary>
    public static void Reject(Action action, string message) => Reject<InvalidOperationException>(action, message);

    /// <summary>Checks that <paramref name="action"/> throws <typeparamref name="TException"/>
    /// (and, when given, that <paramref name="accept"/> holds for it). Other exceptions propagate.</summary>
    public static void Reject<TException>(Action action, string message, Func<TException, bool>? accept = null) where TException : Exception
    {
        bool rejected = false;
        try { action(); }
        catch (TException error) when (accept?.Invoke(error) ?? true) { rejected = true; }
        Check(rejected, message);
    }

    /// <summary>Asynchronous form of <see cref="Reject{TException}"/>.</summary>
    public static async Task RejectAsync<TException>(Func<Task> action, string message, Func<TException, bool>? accept = null) where TException : Exception
    {
        bool rejected = false;
        try { await action(); }
        catch (TException error) when (accept?.Invoke(error) ?? true) { rejected = true; }
        Check(rejected, message);
    }
}

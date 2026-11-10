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

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Counts the crashes of a ready server, and tells when there were too many in a short period.</summary>
/// <param name="timeProvider">The clock.</param>
internal sealed class CrashBudget(TimeProvider timeProvider)
{
    /// <summary>The period in which <see cref="MaxCrashes"/> crashes stop the restarts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>How many crashes in <see cref="Window"/> stop the restarts.</summary>
    public const int MaxCrashes = 3;

    /// <summary>The times of the recent crashes.</summary>
    private readonly Queue<DateTimeOffset> crashes = new();

    /// <summary>Records one crash.</summary>
    /// <returns><see langword="true"/> when the budget is used up, and the engine must not restart the server.</returns>
    public bool RecordCrash()
    {
        var now = timeProvider.GetUtcNow();
        crashes.Enqueue(now);
        while (crashes.Peek() <= now - Window)
        {
            crashes.Dequeue();
        }

        return crashes.Count >= MaxCrashes;
    }
}

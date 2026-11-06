namespace XatLlmEngine.Application.Abstractions;

/// <summary>The largest number of tokens that one provider can use in a rolling period.</summary>
/// <param name="Tokens">The token limit, input and output together.</param>
/// <param name="Period">The length of the rolling period.</param>
public sealed record TokenBudget(long Tokens, TimeSpan Period);

/// <summary>Keeps the token use of each provider over time, so that a budget can apply across engine restarts.</summary>
/// <remarks>Implementations must be thread-safe.</remarks>
public interface IUsageLedger
{
    /// <summary>Gives the tokens that a provider used in the period before now.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="period">The length of the period.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The tokens.</returns>
    long Used(string provider, TimeSpan period, DateTimeOffset now);

    /// <summary>Gives the time when the use of a provider in the rolling period drops below a limit.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="budget">The budget.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The time.</returns>
    DateTimeOffset AvailableAt(string provider, TokenBudget budget, DateTimeOffset now);

    /// <summary>Records tokens that a provider used.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="tokens">The tokens, input and output together.</param>
    /// <param name="now">The current time.</param>
    void Record(string provider, long tokens, DateTimeOffset now);
}

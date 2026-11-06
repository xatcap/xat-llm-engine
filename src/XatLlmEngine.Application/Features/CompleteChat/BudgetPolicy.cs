using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Applies a provider's token budget. See ADR 0011.</summary>
/// <param name="ledger">The token use over time, or <see langword="null"/> when no ledger is available.</param>
/// <param name="timeProvider">The clock.</param>
internal sealed class BudgetPolicy(IUsageLedger? ledger, TimeProvider timeProvider)
{
    /// <summary>Refuses a request when the provider has used its budget.</summary>
    /// <param name="provider">The selected provider.</param>
    /// <returns>The problem, or <see langword="null"/> when the request can go ahead.</returns>
    public EngineError? Check(IChatProvider provider)
    {
        if (Usage(provider) is not { AvailableAt: { } availableAt } use)
        {
            return null;
        }

        var wait = availableAt - timeProvider.GetUtcNow();
        return new EngineError(
            ErrorKind.BudgetExceeded,
            $"The token budget of the provider '{provider.Name}' is used up: {use.Used} of {use.Limit} tokens in the last {use.Period.TotalHours:0} hours. Requests can continue after {availableAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC.",
            wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1));
    }

    /// <summary>Gives the budget use of a provider now. The ledger is read once, so the use and the state always agree.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The use, or <see langword="null"/> when no budget applies to the provider.</returns>
    public BudgetUse? Usage(IChatProvider provider)
    {
        if (ledger is null || provider.Budget is not { } budget)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var used = ledger.Used(provider.Name, budget.Period, now);
        DateTimeOffset? availableAt = used >= budget.Tokens ? ledger.AvailableAt(provider.Name, budget, now) : null;
        return new BudgetUse(used, budget.Tokens, budget.Period, availableAt);
    }

    /// <summary>Records the tokens of a finished request.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="usage">The tokens.</param>
    public void Record(IChatProvider provider, TokenUsage usage)
    {
        if (ledger is not null && provider.Budget is not null)
        {
            ledger.Record(provider.Name, usage.TotalTokens, timeProvider.GetUtcNow());
        }
    }
}

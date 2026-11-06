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
        if (ledger is null || provider.Budget is not { } budget)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var used = ledger.Used(provider.Name, budget.Period, now);
        if (used < budget.Tokens)
        {
            return null;
        }

        var availableAt = ledger.AvailableAt(provider.Name, budget, now);
        var wait = availableAt - now;
        return new EngineError(
            ErrorKind.BudgetExceeded,
            $"The token budget of the provider '{provider.Name}' is used up: {used} of {budget.Tokens} tokens in the last {budget.Period.TotalHours:0} hours. Requests can continue after {availableAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC.",
            wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1));
    }

    /// <summary>Tells whether a provider has used its budget, for the health route.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns><see langword="true"/> when the engine refuses the provider's requests now.</returns>
    public bool IsExhausted(IChatProvider provider) =>
        ledger is not null && provider.Budget is { } budget && ledger.Used(provider.Name, budget.Period, timeProvider.GetUtcNow()) >= budget.Tokens;

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

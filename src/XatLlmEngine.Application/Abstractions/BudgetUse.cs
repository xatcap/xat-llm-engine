namespace XatLlmEngine.Application.Abstractions;

/// <summary>The token budget use of one provider. See ADR 0011.</summary>
/// <param name="Used">The tokens that the provider used in the rolling period before now.</param>
/// <param name="Limit">The token budget.</param>
/// <param name="Period">The length of the rolling period.</param>
/// <param name="AvailableAt">When the budget is used up, the time when requests can continue. Otherwise <see langword="null"/>.</param>
public sealed record BudgetUse(long Used, long Limit, TimeSpan Period, DateTimeOffset? AvailableAt)
{
    /// <summary>Gets a value indicating whether the provider has used its budget.</summary>
    public bool IsExhausted => AvailableAt is not null;
}

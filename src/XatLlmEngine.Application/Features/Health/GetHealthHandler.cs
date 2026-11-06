using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.Providers;

namespace XatLlmEngine.Application.Features.Health;

/// <summary>The overall health of the engine.</summary>
public enum OverallHealth
{
    /// <summary>Every enabled provider can take requests.</summary>
    Healthy,

    /// <summary>At least one enabled provider can take requests, and at least one cannot.</summary>
    Degraded,

    /// <summary>No enabled provider can take requests. This includes an engine with no enabled provider, and an engine that is stopping.</summary>
    Unhealthy,
}

/// <summary>The health of one provider.</summary>
/// <param name="Name">The provider's name.</param>
/// <param name="Status">The provider's status.</param>
public sealed record ProviderHealth(string Name, ProviderStatus Status);

/// <summary>The health of the engine and of each provider.</summary>
/// <param name="Overall">The overall health.</param>
/// <param name="Providers">Each configured provider, enabled or not, in configuration order.</param>
public sealed record EngineHealth(OverallHealth Overall, IReadOnlyList<ProviderHealth> Providers);

/// <summary>Builds the health of the engine. See ADR 0010.</summary>
/// <param name="providers">The enabled providers.</param>
/// <param name="disabled">The providers that are turned off.</param>
/// <param name="ledger">The token use over time, or <see langword="null"/> when no budget can apply.</param>
/// <param name="timeProvider">The clock, or <see langword="null"/> for the system clock.</param>
public sealed class GetHealthHandler(ProviderRegistry providers, IDisabledProviders disabled, IUsageLedger? ledger = null, TimeProvider? timeProvider = null)
{
    /// <summary>The budget rules.</summary>
    private readonly CompleteChat.BudgetPolicy budget = new(ledger, timeProvider ?? TimeProvider.System);

    /// <summary>Builds the health now.</summary>
    /// <param name="stopping">Whether the engine is stopping.</param>
    /// <returns>The health.</returns>
    public EngineHealth Handle(bool stopping)
    {
        var enabled = providers.All.Select(provider => new ProviderHealth(provider.Name, StatusOf(provider, stopping))).ToList();
        var serving = enabled.Count(provider => provider.Status.IsServing);
        var overall = serving == 0 ? OverallHealth.Unhealthy : serving == enabled.Count ? OverallHealth.Healthy : OverallHealth.Degraded;
        var off = disabled.Names.Select(name => new ProviderHealth(name, new ProviderStatus(ProviderState.Disabled)));
        return new EngineHealth(overall, [.. enabled, .. off]);
    }

    /// <summary>Gives the status of one enabled provider. A provider that has used its budget cannot take requests.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="stopping">Whether the engine is stopping.</param>
    /// <returns>The status.</returns>
    private ProviderStatus StatusOf(IChatProvider provider, bool stopping)
    {
        var status = provider.Status;
        return stopping ? new ProviderStatus(ProviderState.Stopping, status.Load)
            : status.IsServing && budget.IsExhausted(provider) ? status with { State = ProviderState.BudgetExhausted }
            : status;
    }

    /// <summary>Tells whether the engine, or one provider, can take requests.</summary>
    /// <param name="health">The health.</param>
    /// <param name="provider">The provider's name, or <see langword="null"/> for the whole engine.</param>
    /// <returns><see langword="true"/> when the engine has a provider that can take requests, or when the named provider can take requests.</returns>
    public static bool IsReady(EngineHealth health, string? provider) =>
        provider is null
            ? health.Overall != OverallHealth.Unhealthy
            : health.Providers.Any(entry => string.Equals(entry.Name, provider, StringComparison.OrdinalIgnoreCase) && entry.Status.IsServing);
}

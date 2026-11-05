using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Reports the local model's stage. Anything other than ready is degraded, not unhealthy, because cloud providers keep working.</summary>
/// <param name="supervisor">Reports the stage.</param>
internal sealed class LlamaServerHealthCheck(LlamaServerSupervisor supervisor) : IHealthCheck
{
    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var description = supervisor.State.ToString().ToLowerInvariant();
        return Task.FromResult(supervisor.State == LlamaServerState.Ready
            ? HealthCheckResult.Healthy(description)
            : HealthCheckResult.Degraded(description));
    }
}

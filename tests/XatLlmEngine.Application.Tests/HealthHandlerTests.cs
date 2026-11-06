using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.Health;
using XatLlmEngine.Application.Features.Providers;

namespace XatLlmEngine.Application.Tests;

/// <summary>Tests the rules for the overall health.</summary>
public class HealthHandlerTests
{
    /// <summary>The overall health depends on how many enabled providers can take requests.</summary>
    /// <param name="states">The states of the enabled providers, separated by ','.</param>
    /// <param name="expected">The expected overall health.</param>
    [Theory]
    [InlineData("", OverallHealth.Unhealthy)]
    [InlineData("Ready", OverallHealth.Healthy)]
    [InlineData("Busy", OverallHealth.Healthy)]
    [InlineData("Ready,Missing", OverallHealth.Degraded)]
    [InlineData("Starting", OverallHealth.Unhealthy)]
    [InlineData("Failed,Unresponsive", OverallHealth.Unhealthy)]
    public void Handle_States_GiveOverallHealth(string states, OverallHealth expected)
    {
        var providers = states.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select((state, index) => new FakeChatProvider($"p{index}") { Status = new ProviderStatus(Enum.Parse<ProviderState>(state)) });

        var health = new GetHealthHandler(new ProviderRegistry(providers), new Disabled("off")).Handle(stopping: false);

        Assert.Equal(expected, health.Overall);
    }

    /// <summary>Disabled providers are listed, but they do not change the overall health.</summary>
    [Fact]
    public void Handle_DisabledProvider_IsListedOnly()
    {
        var health = new GetHealthHandler(new ProviderRegistry([new FakeChatProvider("cloud")]), new Disabled("off")).Handle(stopping: false);

        Assert.Equal(OverallHealth.Healthy, health.Overall);
        Assert.Equal(ProviderState.Disabled, health.Providers.Single(provider => provider.Name == "off").Status.State);
    }

    /// <summary>An engine that is stopping is unhealthy, so that a caller stops sending requests to it.</summary>
    [Fact]
    public void Handle_Stopping_IsUnhealthy()
    {
        var health = new GetHealthHandler(new ProviderRegistry([new FakeChatProvider("cloud")]), new Disabled()).Handle(stopping: true);

        Assert.Equal(OverallHealth.Unhealthy, health.Overall);
        Assert.False(GetHealthHandler.IsReady(health, "cloud"));
    }

    /// <summary>A fixed list of disabled providers.</summary>
    /// <param name="names">The names.</param>
    private sealed class Disabled(params string[] names) : IDisabledProviders
    {
        /// <inheritdoc/>
        public IReadOnlyList<string> Names => names;
    }
}

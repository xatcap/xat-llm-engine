using System.Reflection;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.Health;
using XatLlmEngine.Host.Contracts;

namespace XatLlmEngine.Host.Endpoints;

/// <summary>Implements the health routes. See ADR 0010.</summary>
internal static class HealthEndpoints
{
    /// <summary>The name of the local provider.</summary>
    private const string LocalName = "local";

    /// <summary>The engine version, without the commit that the build adds after '+'.</summary>
    private static readonly string Version =
        (typeof(HealthEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>Maps <c>/health</c>, <c>/health/live</c> and <c>/health/ready</c>.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", Health)
            .WithDescription("Always 200. Read 'status' and 'providers' for the state of each provider.")
            .Produces<HealthResponse>();
        app.MapGet("/health/live", () => Results.Json(new LivenessResponse("alive"), HostJson.Default.LivenessResponse))
            .WithDescription("200 while the engine process runs.")
            .Produces<LivenessResponse>();
        app.MapGet("/health/ready", Ready)
            .WithDescription("200 when the engine, or the provider in 'provider', can take requests. Otherwise 503.")
            .Produces<HealthResponse>()
            .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>Gives the full health. The status is always 200, so that a person or a script can always read the body.</summary>
    /// <param name="handler">The use case.</param>
    /// <param name="lifetime">Tells whether the engine is stopping.</param>
    /// <returns>The HTTP result.</returns>
    private static IResult Health(GetHealthHandler handler, IHostApplicationLifetime lifetime) =>
        Results.Json(ToBody(handler.Handle(lifetime.ApplicationStopping.IsCancellationRequested)), HostJson.Default.HealthResponse);

    /// <summary>Tells whether the engine, or one provider, can take requests.</summary>
    /// <param name="handler">The use case.</param>
    /// <param name="lifetime">Tells whether the engine is stopping.</param>
    /// <param name="provider">The provider's name, or <see langword="null"/> for the whole engine.</param>
    /// <returns>200 when ready, otherwise 503. The body is the full health.</returns>
    private static IResult Ready(GetHealthHandler handler, IHostApplicationLifetime lifetime, string? provider)
    {
        var health = handler.Handle(lifetime.ApplicationStopping.IsCancellationRequested);
        var status = GetHealthHandler.IsReady(health, provider) ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        return Results.Json(ToBody(health), HostJson.Default.HealthResponse, statusCode: status);
    }

    /// <summary>Builds the response body.</summary>
    /// <param name="health">The health.</param>
    /// <returns>The body.</returns>
    private static HealthResponse ToBody(EngineHealth health)
    {
        var providers = health.Providers.Select(ToBody).ToList();
        var local = providers.FirstOrDefault(entry => entry.Name == LocalName);
        return new HealthResponse(
            ToWire(health.Overall),
            Version,
            local?.State ?? ToWire(ProviderState.Disabled),
            local?.Message ?? Describe(LocalName, ProviderState.Disabled),
            providers);
    }

    /// <summary>Builds the health of one provider.</summary>
    /// <param name="provider">The provider's health.</param>
    /// <returns>The body.</returns>
    private static ProviderHealthResponse ToBody(ProviderHealth provider)
    {
        var load = provider.Status.Load;
        return new ProviderHealthResponse(
            provider.Name,
            ToWire(provider.Status.State),
            Describe(provider.Name, provider.Status.State),
            load?.Active,
            load?.Queued,
            load?.Capacity,
            load?.QueueLimit);
    }

    /// <summary>Converts the overall health to its public value. These values are part of the API, so they never come from enum names.</summary>
    /// <param name="health">The overall health.</param>
    /// <returns>The value.</returns>
    private static string ToWire(OverallHealth health) => health switch
    {
        OverallHealth.Healthy => "healthy",
        OverallHealth.Degraded => "degraded",
        OverallHealth.Unhealthy => "unhealthy",
    };

    /// <summary>Converts a provider state to its public value. These values are part of the API, so they never come from enum names.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The value.</returns>
    private static string ToWire(ProviderState state) => state switch
    {
        ProviderState.Ready => "ready",
        ProviderState.Busy => "busy",
        ProviderState.Starting => "starting",
        ProviderState.Restarting => "restarting",
        ProviderState.Unresponsive => "unresponsive",
        ProviderState.Missing => "missing",
        ProviderState.Failed => "failed",
        ProviderState.Stopping => "stopping",
        ProviderState.Disabled => "disabled",
        ProviderState.BudgetExhausted => "budget_exhausted",
    };

    /// <summary>Explains a provider state.</summary>
    /// <param name="name">The provider's name.</param>
    /// <param name="state">The state.</param>
    /// <returns>The explanation.</returns>
    private static string Describe(string name, ProviderState state) => (name == LocalName, state) switch
    {
        (true, ProviderState.Ready) => "The local model is ready.",
        (true, ProviderState.Busy) => "The local model is ready, and all its slots are in use. New requests wait in the queue.",
        (true, ProviderState.Starting) => "The local model is loading. Local requests return 503 until it is ready.",
        (true, ProviderState.Restarting) => "llama-server stopped unexpectedly, and the engine is restarting it. Local requests return 503 until then.",
        (true, ProviderState.Unresponsive) => "llama-server is busy and does not answer its health checks. New local requests return 503 until it answers again. Requests in progress continue.",
        (true, ProviderState.Missing) => "The model or server file is missing. Local requests return 502. Install the file, then restart the engine.",
        (true, ProviderState.Failed) => "The local model failed. Local requests return 502 with the cause, and the engine log has the details. Restart the engine to try again.",
        (true, ProviderState.Disabled) => "The local model is turned off in settings.",
        (false, ProviderState.Ready) => $"The provider '{name}' is enabled. The engine does not test cloud providers.",
        (false, ProviderState.Disabled) => $"The provider '{name}' has no API key, so it is turned off.",
        (_, ProviderState.Stopping) => "The engine is stopping.",
        (_, ProviderState.BudgetExhausted) => $"The provider '{name}' has used its token budget. Requests return 429 until the use drops below the budget.",
        (false, _) => $"The provider '{name}' cannot take requests now.",
    };
}

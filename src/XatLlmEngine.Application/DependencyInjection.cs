using Microsoft.Extensions.DependencyInjection;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Diagnostics;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Features.Health;
using XatLlmEngine.Application.Features.ListModels;
using XatLlmEngine.Application.Features.Providers;

namespace XatLlmEngine.Application;

/// <summary>Registers the Application layer.</summary>
public static class DependencyInjection
{
    /// <summary>Adds the use cases. They hold no per-request state, so they are singletons.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same collection, so that the caller can chain calls.</returns>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<EngineTelemetry>();
        services.AddSingleton(provider => new ProviderRegistry(provider.GetServices<IChatProvider>()));
        services.AddSingleton<CompleteChatHandler>();
        services.AddSingleton<StreamChatHandler>();
        services.AddSingleton<ListModelsHandler>();
        services.AddSingleton<GetHealthHandler>();
        return services;
    }
}

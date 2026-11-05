using Microsoft.Extensions.DependencyInjection;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Features.ListModels;

namespace XatLlmEngine.Application;

/// <summary>Registers the Application layer.</summary>
public static class DependencyInjection
{
    /// <summary>Adds the use cases. They hold no per-request state, so they are singletons.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same collection, so calls can be chained.</returns>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<CompleteChatHandler>();
        services.AddSingleton<StreamChatHandler>();
        services.AddSingleton<ListModelsHandler>();
        return services;
    }
}

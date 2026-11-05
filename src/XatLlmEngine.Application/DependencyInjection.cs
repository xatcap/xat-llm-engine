using Microsoft.Extensions.DependencyInjection;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Features.ListModels;
using XatLlmEngine.Application.Features.SystemPrompts;

namespace XatLlmEngine.Application;

/// <summary>Registers the Application layer.</summary>
public static class DependencyInjection
{
    /// <summary>Adds the use cases. They hold no per-request state, so they are singletons.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same collection, so that the caller can chain calls.</returns>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // The providers are registered only as IChatProvider, so automatic injection of ISystemPromptSettings would give an empty list.
        services.AddSingleton(provider => new SystemPromptStore(provider.GetServices<IChatProvider>()));
        services.AddSingleton<ReplaceSystemPromptHandler>();
        services.AddSingleton<AppendSystemPromptHandler>();
        services.AddSingleton<CompleteChatHandler>();
        services.AddSingleton<StreamChatHandler>();
        services.AddSingleton<ListModelsHandler>();
        return services;
    }
}

using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>Holds one adapter factory for each wire protocol.</summary>
internal static class ChatProviderFactories
{
    /// <summary>Gets the factory of each protocol.</summary>
    public static FrozenDictionary<ProviderType, IChatProviderFactory> ByType { get; } =
        new IChatProviderFactory[] { new OpenAiCompatibleFactory() }.ToFrozenDictionary(factory => factory.Type);

    /// <summary>Gets the factory of a protocol.</summary>
    /// <param name="name">The provider's name, for the message.</param>
    /// <param name="type">The protocol.</param>
    /// <returns>The factory.</returns>
    /// <exception cref="InvalidOperationException">No factory serves the protocol.</exception>
    public static IChatProviderFactory For(string name, ProviderType type) =>
        ByType.TryGetValue(type, out var factory)
            ? factory
            : throw new InvalidOperationException($"The engine has no adapter for the Type '{type}' of provider '{name}'.");

    /// <summary>Creates the transport of a cloud provider, with the time limits from its settings.</summary>
    /// <typeparam name="TCategory">The log category.</typeparam>
    /// <param name="name">The provider's name.</param>
    /// <param name="options">The provider's settings.</param>
    /// <param name="httpClientName">The named HTTP client.</param>
    /// <param name="services">The application services.</param>
    /// <returns>The transport.</returns>
    private static ProviderTransport Transport<TCategory>(string name, ProviderOptions options, string httpClientName, IServiceProvider services) =>
        new(
            new TransportSettings(
                name,
                ProviderKind.Cloud,
                httpClientName,
                TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
                TimeSpan.FromSeconds(options.ReadIdleTimeoutSeconds)),
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<TCategory>>());

    /// <summary>Creates adapters for the OpenAI chat completions protocol.</summary>
    private sealed class OpenAiCompatibleFactory : IChatProviderFactory
    {
        /// <inheritdoc/>
        public ProviderType Type => ProviderType.OpenAiCompatible;

        /// <inheritdoc/>
        public IChatProvider Create(string name, ProviderOptions options, string httpClientName, IServiceProvider services) =>
            new OpenAiCompatibleChatProvider(name, options, new OpenAiClient(Transport<OpenAiClient>(name, options, httpClientName, services), options.RequestFields.Contains("stream_options", StringComparer.Ordinal)));
    }
}

using XatLlmEngine.Application.Abstractions;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>Creates the adapter for one wire protocol. A new protocol needs a new <see cref="ProviderType"/> value and one factory.</summary>
internal interface IChatProviderFactory
{
    /// <summary>Gets the protocol that this factory serves.</summary>
    ProviderType Type { get; }

    /// <summary>Creates the adapter for one configured provider.</summary>
    /// <param name="name">The provider's name.</param>
    /// <param name="options">The provider's validated settings.</param>
    /// <param name="httpClientName">The named HTTP client of the provider.</param>
    /// <param name="services">The application services.</param>
    /// <returns>The adapter.</returns>
    IChatProvider Create(string name, ProviderOptions options, string httpClientName, IServiceProvider services);
}

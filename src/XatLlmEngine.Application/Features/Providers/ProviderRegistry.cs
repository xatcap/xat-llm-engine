using XatLlmEngine.Application.Abstractions;

namespace XatLlmEngine.Application.Features.Providers;

/// <summary>Holds every enabled provider, by name. The use cases share one registry.</summary>
public sealed class ProviderRegistry
{
    /// <summary>The providers by name. Names ignore case, because configuration keys do too.</summary>
    private readonly Dictionary<string, IChatProvider> byName;

    /// <summary>Initializes a new instance of the <see cref="ProviderRegistry"/> class.</summary>
    /// <param name="providers">Every provider enabled in this deployment, in configuration order.</param>
    /// <exception cref="InvalidOperationException">Two providers have the same name.</exception>
    public ProviderRegistry(IEnumerable<IChatProvider> providers)
    {
        All = [.. providers];
        var duplicates = All.GroupBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException($"More than one provider has the name: {string.Join(", ", duplicates)}. Each provider must have its own name.");
        }

        byName = All.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets every provider, in configuration order.</summary>
    public IReadOnlyList<IChatProvider> All { get; }

    /// <summary>Finds a provider by name.</summary>
    /// <param name="name">The name. Case is ignored.</param>
    /// <param name="provider">The provider, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a provider has this name.</returns>
    public bool TryGet(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IChatProvider? provider) =>
        byName.TryGetValue(name, out provider);
}

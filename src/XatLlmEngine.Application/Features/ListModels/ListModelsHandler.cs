using XatLlmEngine.Application.Features.Providers;

namespace XatLlmEngine.Application.Features.ListModels;

/// <summary>One advertised model.</summary>
/// <param name="Provider">The provider's name.</param>
/// <param name="Model">The model name inside the provider.</param>
public sealed record ModelEntry(string Provider, string Model)
{
    /// <summary>Gets the full reference that callers send, written as <c>provider/model</c>.</summary>
    public string Id => $"{Provider}/{Model}";
}

/// <summary>Lists the models this deployment advertises.</summary>
/// <param name="providers">The enabled providers.</param>
public sealed class ListModelsHandler(ProviderRegistry providers)
{
    /// <summary>Builds the model list.</summary>
    /// <returns>Each model, in the order of the provider configuration.</returns>
    public IReadOnlyList<ModelEntry> Handle() =>
        [.. providers.All.SelectMany(provider => provider.Models.Select(model => new ModelEntry(provider.Name, model)))];
}

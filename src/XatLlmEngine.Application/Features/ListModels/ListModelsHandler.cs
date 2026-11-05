using XatLlmEngine.Application.Abstractions;

namespace XatLlmEngine.Application.Features.ListModels;

/// <summary>Lists the models this deployment advertises.</summary>
/// <param name="providers">Every provider enabled in this deployment.</param>
public sealed class ListModelsHandler(IEnumerable<IChatProvider> providers)
{
    /// <summary>Builds the model list.</summary>
    /// <returns>Each model written as <c>provider/model</c>, in the order providers were configured.</returns>
    public IReadOnlyList<string> Handle() =>
        [.. providers.SelectMany(provider => provider.Models.Select(model => $"{provider.Name}/{model}"))];
}

namespace XatLlmEngine.Application.Abstractions;

/// <summary>The names of the providers that the engine has. Callers write a name before the model name, for example <c>local/ministral-3-3b</c>.</summary>
public static class ProviderNames
{
    /// <summary>The name of the main local model, for chat.</summary>
    public const string Local = "local";

    /// <summary>The name of the small local model that expands search queries.</summary>
    public const string QueryExpansion = "query-expansion";
}

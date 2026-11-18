namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Settings for the small local model that expands search queries. The rules are the same as for the main model. Only the default values differ.</summary>
public sealed class QueryExpansionOptions : LlamaCppOptions
{
    /// <summary>The configuration section that holds these settings.</summary>
    public new const string SectionName = "QueryExpansionModel";

    /// <summary>Initializes a new instance of the <see cref="QueryExpansionOptions"/> class with the defaults of the small model.</summary>
    /// <remarks>
    /// The defaults replace those of the main model, so that a missing section never loads the main model a second time.
    /// The list settings, <see cref="LlamaCppOptions.ExtraArguments"/> and <see cref="LlamaCppOptions.RequestFields"/>, have no defaults in code, because the configuration binder adds to a list and does not replace it. <c>appsettings.json</c> holds them.
    /// </remarks>
    public QueryExpansionOptions()
    {
        ModelName = "qwen3-0.6b";
        ModelPath = "models/Qwen3-0.6B-Q4_K_M.gguf";
        ContextSize = 4096;
        DefaultTemperature = 0.3;
        MaxTokensLimit = 256;
        MaxQueuedRequests = 16;
    }
}

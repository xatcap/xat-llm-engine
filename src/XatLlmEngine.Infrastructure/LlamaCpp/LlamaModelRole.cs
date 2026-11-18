using XatLlmEngine.Application.Abstractions;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>The identity of one local model. Two models run at the same time, so each one needs its own names, files and metric tag.</summary>
/// <param name="ProviderName">The provider name that callers write before the model name.</param>
/// <param name="SectionName">The configuration section of the model, for messages.</param>
/// <param name="PidFileName">The name of the PID file in the data folder. Each model has its own file, so that one supervisor never stops the server of another.</param>
internal sealed record LlamaModelRole(string ProviderName, string SectionName, string PidFileName)
{
    /// <summary>Gets the role of the main model, for chat.</summary>
    public static LlamaModelRole Main { get; } = new(ProviderNames.Local, LlamaCppOptions.SectionName, LlamaServerPidFile.FileName);

    /// <summary>Gets the role of the small model that expands search queries.</summary>
    public static LlamaModelRole QueryExpansion { get; } = new(ProviderNames.QueryExpansion, QueryExpansionOptions.SectionName, "llama-server-query-expansion.pid");

    /// <summary>Gets a value that tells whether this is the main model. Only the main model reads the PID file of the engine versions before the data folder.</summary>
    public bool IsMain => ProviderName == ProviderNames.Local;

    /// <summary>Gets the name of the model in log messages and metric tags. It is the provider name.</summary>
    public string Label => ProviderName;
}

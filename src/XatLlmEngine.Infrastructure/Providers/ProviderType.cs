namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>The wire protocol a cloud provider speaks.</summary>
public enum ProviderType
{
    /// <summary>The OpenAI chat completions protocol, also offered by many other vendors.</summary>
    OpenAiCompatible,
}

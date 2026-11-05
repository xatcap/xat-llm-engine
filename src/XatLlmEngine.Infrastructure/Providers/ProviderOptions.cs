using System.ComponentModel.DataAnnotations;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>Settings for one cloud provider. The configuration key under <c>Providers</c> becomes its name.</summary>
public sealed class ProviderOptions
{
    /// <summary>The configuration section that holds all cloud providers.</summary>
    public const string SectionName = "Providers";

    /// <summary>Gets the wire protocol of the provider.</summary>
    public ProviderType Type { get; init; } = ProviderType.OpenAiCompatible;

    /// <summary>Gets the base address, for example <c>https://api.openai.com/v1</c>.</summary>
    [Required]
    public Uri? BaseUrl { get; init; }

    /// <summary>Gets the secret API key. A provider without a key is not enabled. Set it through an environment variable or secret store.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Gets the model names to advertise. Callers can also request names not listed here.</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>Gets a value indicating whether to send the token limit as <c>max_completion_tokens</c>, which OpenAI requires, instead of <c>max_tokens</c>.</summary>
    public bool UseMaxCompletionTokens { get; init; }

    /// <summary>Gets the token limit to use when a caller sends none. Anthropic requires a limit on every request.</summary>
    [Range(1, int.MaxValue)]
    public int DefaultMaxTokens { get; init; } = 4096;

    /// <summary>Gets how long to wait for a provider to begin responding, in seconds, across all retries. A non-streaming answer only begins when it is complete, so this must be generous.</summary>
    [Range(1, 86_400)]
    public int RequestTimeoutSeconds { get; init; } = 600;

    /// <summary>Gets how many times to retry when a provider returns a 429 or 5xx status or cannot be reached. Retries only happen before a response starts.</summary>
    [Range(0, 10)]
    public int MaxRetries { get; init; } = 2;
}

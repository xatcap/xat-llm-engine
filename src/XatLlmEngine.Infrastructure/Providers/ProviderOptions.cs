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

    /// <summary>Gets the request fields this provider accepts, in addition to <c>model</c> and <c>stream</c>. This list is its request template. A new vendor field needs a change only here.</summary>
    [MinLength(1, ErrorMessage = "RequestFields must list at least one field.")]
    public IReadOnlyList<string> RequestFields { get; init; } = [];

    /// <summary>Gets the token limit to use when a caller sends none. Anthropic requires a limit on every request.</summary>
    [Range(1, int.MaxValue)]
    public int DefaultMaxTokens { get; init; } = 4096;

    /// <summary>Gets how long to wait for the start of a provider's response, in seconds, across all retries. A non-streaming answer starts only when it is complete, so make this time long.</summary>
    [Range(1, 86_400)]
    public int RequestTimeoutSeconds { get; init; } = 600;

    /// <summary>Gets how many retries to do when a provider returns a 429 or 5xx status, or when the engine cannot reach it. Retries occur only before a response starts.</summary>
    [Range(0, 10)]
    public int MaxRetries { get; init; } = 2;
}

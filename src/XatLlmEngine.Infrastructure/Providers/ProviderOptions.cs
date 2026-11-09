using System.ComponentModel.DataAnnotations;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>Settings for one cloud provider. The configuration key under <c>Providers</c> becomes its name.</summary>
public sealed class ProviderOptions
{
    /// <summary>The configuration section that holds all cloud providers.</summary>
    public const string SectionName = "Providers";

    /// <summary>Gets the wire protocol of the provider. It has no default, so that a missing or wrong value stops the engine at startup.</summary>
    [Required]
    public ProviderType? Type { get; init; }

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

    /// <summary>
    /// Gets the largest number of output tokens that a caller can ask for, or 0 for no limit. A request that asks for more gets 400.
    /// A request with no token field gets this limit. The engine uses the first of <c>max_tokens</c> and <c>max_completion_tokens</c>
    /// that <see cref="RequestFields"/> lists.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int MaxTokensLimit { get; init; }

    /// <summary>Gets how long to wait for the start of a provider's response, in seconds. A non-streaming answer starts only when it is complete, so make this time long. The engine never retries a request.</summary>
    [Range(1, 86_400)]
    public int RequestTimeoutSeconds { get; init; } = 600;

    /// <summary>Gets how long the engine waits for more data after the response has started, in seconds. This applies to each read of the body and to the time between stream events. Reasoning models can send nothing for minutes, so make this time long.</summary>
    [Range(1, 86_400)]
    public int ReadIdleTimeoutSeconds { get; init; } = 300;
}

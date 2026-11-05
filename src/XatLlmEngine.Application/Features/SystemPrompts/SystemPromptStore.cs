using System.Collections.Concurrent;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.SystemPrompts;

/// <summary>Holds the current system prompt of each provider. The prompts stay in memory, so a restart returns each provider to its configured prompt.</summary>
public sealed class SystemPromptStore
{
    /// <summary>The prompts by provider name. A blank prompt means that the engine adds nothing. Names ignore case, because configuration keys do too.</summary>
    private readonly ConcurrentDictionary<string, string> prompts;

    /// <summary>Initializes a new instance of the <see cref="SystemPromptStore"/> class.</summary>
    /// <param name="providers">Every provider enabled in this deployment.</param>
    public SystemPromptStore(IEnumerable<IChatProvider> providers)
    {
        prompts = new ConcurrentDictionary<string, string>(
            providers.Select(provider => KeyValuePair.Create(provider.Name, provider.SystemPrompt ?? string.Empty)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the prompt to add to a provider's requests.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <returns>The prompt, or <see langword="null"/> when the provider has none.</returns>
    public string? Get(string provider) =>
        prompts.TryGetValue(provider, out var prompt) && !string.IsNullOrWhiteSpace(prompt) ? prompt : null;

    /// <summary>Replaces a provider's prompt. An empty prompt clears it.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="prompt">The new prompt.</param>
    /// <returns>The full prompt, or a not-found failure.</returns>
    public Result<string> Replace(string provider, string prompt)
    {
        if (!prompts.ContainsKey(provider))
        {
            return NotFound(provider);
        }

        prompts[provider] = prompt;
        return Result<string>.Ok(prompt);
    }

    /// <summary>Adds text to the end of a provider's prompt. A newline separates the text from any existing prompt.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="text">The text to add. Empty text changes nothing.</param>
    /// <returns>The full prompt, or a not-found failure.</returns>
    public Result<string> Append(string provider, string text)
    {
        if (!prompts.ContainsKey(provider))
        {
            return NotFound(provider);
        }

        // AddOrUpdate retries when another caller changes the prompt at the same time, so that no text is lost.
        return Result<string>.Ok(prompts.AddOrUpdate(
            provider,
            _ => text,
            (_, current) => current.Length == 0 || text.Length == 0 ? current + text : $"{current}\n{text}"));
    }

    /// <summary>Builds the failure for an unknown provider.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <returns>A not-found failure.</returns>
    private static Result<string> NotFound(string provider) => Result<string>.Fail(new EngineError(
        ErrorKind.NotFound,
        $"The provider '{provider}' is not available. Check its name, and check that the configuration has its API key."));
}

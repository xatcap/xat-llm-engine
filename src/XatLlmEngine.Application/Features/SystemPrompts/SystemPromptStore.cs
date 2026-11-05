using System.Collections.Concurrent;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.SystemPrompts;

/// <summary>Holds the current system prompt of each provider, and keeps each prompt inside its limit. The prompts stay in memory, so a restart returns each provider to its configured prompt.</summary>
public sealed class SystemPromptStore
{
    /// <summary>The prompt and limit of each provider. Names ignore case, because configuration keys do too.</summary>
    private readonly ConcurrentDictionary<string, Entry> entries;

    /// <summary>Initializes a new instance of the <see cref="SystemPromptStore"/> class.</summary>
    /// <param name="providers">The settings of every provider enabled in this deployment.</param>
    public SystemPromptStore(IEnumerable<ISystemPromptSettings> providers)
    {
        entries = new ConcurrentDictionary<string, Entry>(
            providers.Select(provider => KeyValuePair.Create(provider.Name, new Entry(provider.InitialSystemPrompt ?? string.Empty, provider.MaxSystemPromptLength))),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the prompt to add to a provider's requests.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <returns>The prompt, or <see langword="null"/> when the prompt is blank or the provider is unknown.</returns>
    public string? Get(string provider) =>
        entries.TryGetValue(provider, out var entry) && !string.IsNullOrWhiteSpace(entry.Prompt) ? entry.Prompt : null;

    /// <summary>Changes a provider's prompt as one step, so that the limit always holds and no change is lost.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="change">Builds the new prompt from the current prompt. This can run more than once, so it must have no side effects.</param>
    /// <returns>The full prompt, a not-found failure, or an invalid-request failure when the prompt is too long. A failure leaves the prompt unchanged.</returns>
    public Result<string> Change(string provider, Func<string, string> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        while (true)
        {
            if (!entries.TryGetValue(provider, out var current))
            {
                return Result<string>.Fail(new EngineError(
                    ErrorKind.NotFound,
                    $"The provider '{provider}' is not available. Check its name, and check that the configuration has its API key."));
            }

            var next = change(current.Prompt);
            if (next.Length > current.Limit)
            {
                return Result<string>.Fail(new EngineError(
                    ErrorKind.InvalidRequest,
                    $"The system prompt of provider '{provider}' cannot be longer than {current.Limit} characters. The prompt did not change."));
            }

            // TryUpdate fails when another caller changes the prompt at the same time. The loop then tries again with the new prompt.
            if (entries.TryUpdate(provider, current with { Prompt = next }, current))
            {
                return Result<string>.Ok(next);
            }
        }
    }

    /// <summary>One provider's values.</summary>
    /// <param name="Prompt">The current prompt. A blank prompt means that the engine adds nothing.</param>
    /// <param name="Limit">The longest prompt, in characters.</param>
    private sealed record Entry(string Prompt, int Limit);
}

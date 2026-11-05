using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.SystemPrompts;

/// <summary>Replaces a provider's system prompt. An empty prompt clears it.</summary>
/// <param name="store">The stored prompts.</param>
public sealed class ReplaceSystemPromptHandler(SystemPromptStore store)
{
    /// <summary>Applies the change.</summary>
    /// <param name="command">The caller's request.</param>
    /// <returns>The full prompt, or the expected failure.</returns>
    public Result<string> Handle(SystemPromptCommand command) =>
        store.Change(command.Provider, _ => command.Prompt);
}

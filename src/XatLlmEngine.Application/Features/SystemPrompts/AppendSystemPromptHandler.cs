using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.SystemPrompts;

/// <summary>Adds text to the end of a provider's system prompt. A newline separates the text from any existing prompt, and empty text changes nothing.</summary>
/// <param name="store">The stored prompts.</param>
public sealed class AppendSystemPromptHandler(SystemPromptStore store)
{
    /// <summary>Applies the change.</summary>
    /// <param name="command">The caller's request.</param>
    /// <returns>The full prompt, or the expected failure.</returns>
    public Result<string> Handle(SystemPromptCommand command) =>
        store.Change(command.Provider, current => current.Length == 0 || command.Prompt.Length == 0 ? current + command.Prompt : $"{current}\n{command.Prompt}");
}

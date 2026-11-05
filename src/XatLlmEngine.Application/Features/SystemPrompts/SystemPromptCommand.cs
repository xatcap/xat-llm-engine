namespace XatLlmEngine.Application.Features.SystemPrompts;

/// <summary>A request to change a provider's system prompt.</summary>
/// <param name="Provider">The provider's name.</param>
/// <param name="Prompt">The new prompt, or the text to add to the end of the prompt.</param>
public sealed record SystemPromptCommand(string Provider, string Prompt);

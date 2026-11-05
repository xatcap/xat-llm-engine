namespace XatLlmEngine.Host.Contracts;

/// <summary>The body of a request that changes a system prompt.</summary>
/// <param name="Prompt">The new prompt, or the text to add to the end of the prompt.</param>
internal sealed record SystemPromptRequest(string? Prompt);

/// <summary>A provider's system prompt after a change.</summary>
/// <param name="Provider">The provider's name.</param>
/// <param name="Prompt">The full prompt. An empty value means that the engine adds nothing, so the model's own default applies, if it has one.</param>
internal sealed record SystemPromptResponse(string Provider, string Prompt);

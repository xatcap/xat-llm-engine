namespace XatLlmEngine.Application.Abstractions;

/// <summary>The system prompt settings of one provider. The stored prompts need only these values, and not the provider's chat operations.</summary>
public interface ISystemPromptSettings
{
    /// <summary>Gets the name callers use as the prefix of a model, for example <c>anthropic</c>.</summary>
    string Name { get; }

    /// <summary>Gets the system prompt from the configuration, which applies from the start of the engine until a caller changes it.</summary>
    string? InitialSystemPrompt { get; }

    /// <summary>Gets the longest system prompt this provider accepts, in characters.</summary>
    int MaxSystemPromptLength { get; }
}

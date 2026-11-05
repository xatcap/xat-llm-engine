using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>A caller's request for a chat answer, not yet validated.</summary>
/// <param name="Model">The target written as <c>provider/model</c>.</param>
/// <param name="Messages">The conversation so far.</param>
/// <param name="Temperature">Sampling temperature, which must not be negative.</param>
/// <param name="TopP">Nucleus sampling value, between 0 and 1.</param>
/// <param name="MaxTokens">The most tokens to generate, which must be positive.</param>
/// <param name="Stop">Text sequences that end generation.</param>
public sealed record CompleteChatCommand(
    string? Model,
    IReadOnlyList<ChatMessage> Messages,
    double? Temperature = null,
    double? TopP = null,
    int? MaxTokens = null,
    IReadOnlyList<string>? Stop = null);

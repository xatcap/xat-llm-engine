using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Abstractions;

/// <summary>A validated chat request, addressed to one provider.</summary>
/// <param name="Model">The model name inside the provider, without the provider prefix.</param>
/// <param name="Messages">The conversation so far. It always contains at least one message.</param>
/// <param name="Temperature">Sampling temperature, or <see langword="null"/> to use the provider's default.</param>
/// <param name="TopP">Nucleus sampling value, or <see langword="null"/> to use the provider's default.</param>
/// <param name="MaxTokens">The most tokens to generate, or <see langword="null"/> to use the provider's default.</param>
/// <param name="Stop">Text sequences that end generation. It is empty when the caller sent none.</param>
public sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    double? Temperature,
    double? TopP,
    int? MaxTokens,
    IReadOnlyList<string> Stop);

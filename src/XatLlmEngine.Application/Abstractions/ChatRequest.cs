using System.Text.Json.Nodes;

namespace XatLlmEngine.Application.Abstractions;

/// <summary>A chat request addressed to one provider, in that provider's own format.</summary>
/// <param name="Model">The model name inside the provider, without the provider prefix.</param>
/// <param name="Body">The caller's JSON. It holds only fields the provider lists in <see cref="IChatProvider.RequestFields"/>, plus <c>model</c> and <c>stream</c>.</param>
/// <param name="SystemPrompt">The provider's stored system prompt, which the adapter adds to the body in the provider's format. It is <see langword="null"/> when the provider has none.</param>
public sealed record ChatRequest(string Model, JsonObject Body, string? SystemPrompt = null);

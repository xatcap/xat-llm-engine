using System.Text.Json.Nodes;

namespace XatLlmEngine.Application.Abstractions;

/// <summary>A chat request addressed to one provider, in that provider's own format.</summary>
/// <param name="Model">The model name inside the provider, without the provider prefix.</param>
/// <param name="Body">
/// A copy of the caller's JSON. It holds only fields the provider lists in <see cref="IChatProvider.RequestFields"/>, plus <c>model</c> and <c>stream</c>.
/// Each request has its own copy, so the provider owns it and can change it, for example to add a default value.
/// </param>
public sealed record ChatRequest(string Model, JsonObject Body);

using System.Text.Json;
using System.Text.Json.Serialization;
using XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>Source-generated JSON handling for every provider wire type. The protocol uses snake_case.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UpstreamErrorEnvelope))]
[JsonSerializable(typeof(OpenAiChatResponse))]
internal sealed partial class ProviderJson : JsonSerializerContext;

/// <summary>The error body shared by the OpenAI and llama-server protocols.</summary>
/// <param name="Error">The error details.</param>
internal sealed record UpstreamErrorEnvelope(UpstreamErrorBody? Error);

/// <summary>The details of an upstream error.</summary>
/// <param name="Message">The provider's description of the problem.</param>
internal sealed record UpstreamErrorBody(string? Message);

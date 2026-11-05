using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace XatLlmEngine.Host.Contracts;

/// <summary>Source-generated JSON handling for the public API, which uses snake_case like the OpenAI format.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatCompletionChunkResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ModelListResponse))]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class HostJson : JsonSerializerContext;

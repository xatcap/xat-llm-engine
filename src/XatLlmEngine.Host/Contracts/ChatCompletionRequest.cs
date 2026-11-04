using System.Text.Json;

namespace XatLlmEngine.Host.Contracts;

/// <summary>The body of <c>POST /v1/chat/completions</c>, following the OpenAI format.</summary>
/// <param name="Model">The target written as <c>provider/model</c>.</param>
/// <param name="Messages">The conversation.</param>
/// <param name="Stream">Whether to stream the answer.</param>
/// <param name="StreamOptions">Streaming settings.</param>
/// <param name="Temperature">Sampling temperature.</param>
/// <param name="TopP">Nucleus sampling value.</param>
/// <param name="MaxTokens">Token limit, using the older field name.</param>
/// <param name="MaxCompletionTokens">Token limit, using the newer field name, which takes priority.</param>
/// <param name="Stop">Stop sequences: one string or an array of strings.</param>
/// <param name="Tools">Tool definitions. They are read only so that they can be rejected, instead of being silently ignored.</param>
/// <param name="N">How many answers to generate. Only one is supported.</param>
/// <param name="ResponseFormat">The requested format. Only plain text is supported.</param>
internal sealed record ChatCompletionRequest(
    string? Model,
    IReadOnlyList<ChatCompletionRequestMessage>? Messages,
    bool? Stream = null,
    ChatCompletionStreamOptions? StreamOptions = null,
    double? Temperature = null,
    double? TopP = null,
    int? MaxTokens = null,
    int? MaxCompletionTokens = null,
    JsonElement? Stop = null,
    JsonElement? Tools = null,
    int? N = null,
    ChatCompletionResponseFormat? ResponseFormat = null);

/// <summary>A message in the request.</summary>
/// <param name="Role">The author's role.</param>
/// <param name="Content">The text: one string or an array of content parts.</param>
internal sealed record ChatCompletionRequestMessage(string? Role, JsonElement? Content);

/// <summary>Streaming settings.</summary>
/// <param name="IncludeUsage">Whether to send token counts at the end of the stream.</param>
internal sealed record ChatCompletionStreamOptions(bool? IncludeUsage = null);

/// <summary>The requested response format.</summary>
/// <param name="Type">The format name.</param>
internal sealed record ChatCompletionResponseFormat(string? Type);

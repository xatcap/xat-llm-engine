using System.Text.Json.Serialization;

namespace XatLlmEngine.Host.Contracts;

/// <summary>A complete answer in the OpenAI format.</summary>
/// <param name="Id">A unique ID for this answer.</param>
/// <param name="Object">Always <c>chat.completion</c>.</param>
/// <param name="Created">The creation time in Unix seconds.</param>
/// <param name="Model">The full <c>provider/model</c> reference.</param>
/// <param name="Choices">The answers. There is always exactly one.</param>
/// <param name="Usage">Token counts, when the provider reported them.</param>
internal sealed record ChatCompletionResponse(
    string Id,
    string Object,
    long Created,
    string Model,
    IReadOnlyList<ChatCompletionChoice> Choices,
    ChatCompletionUsage? Usage);

/// <summary>One complete answer.</summary>
/// <param name="Index">The answer's position, which is always 0.</param>
/// <param name="Message">The answer's message.</param>
/// <param name="FinishReason">Why generation stopped.</param>
internal sealed record ChatCompletionChoice(int Index, ChatCompletionMessage Message, string FinishReason);

/// <summary>A message in a response. Both values are optional inside a stream.</summary>
/// <param name="Role">The author's role.</param>
/// <param name="Content">The text.</param>
internal sealed record ChatCompletionMessage(string? Role, string? Content);

/// <summary>Token counts in the OpenAI format.</summary>
/// <param name="PromptTokens">Tokens in the prompt.</param>
/// <param name="CompletionTokens">Tokens generated.</param>
/// <param name="TotalTokens">The sum of both.</param>
internal sealed record ChatCompletionUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

/// <summary>One piece of a stream, in the OpenAI format.</summary>
/// <param name="Id">The ID shared by all pieces of this answer.</param>
/// <param name="Object">Always <c>chat.completion.chunk</c>.</param>
/// <param name="Created">The creation time in Unix seconds.</param>
/// <param name="Model">The full <c>provider/model</c> reference.</param>
/// <param name="Choices">The changes. This is empty on the final usage piece.</param>
/// <param name="Usage">Token counts, on the final usage piece only.</param>
internal sealed record ChatCompletionChunkResponse(
    string Id,
    string Object,
    long Created,
    string Model,
    IReadOnlyList<ChatCompletionChunkChoice> Choices,
    ChatCompletionUsage? Usage);

/// <summary>The change in one stream piece.</summary>
/// <param name="Index">The answer's position, which is always 0.</param>
/// <param name="Delta">The new text.</param>
/// <param name="FinishReason">Why generation stopped. Until then, the engine writes an explicit null, because clients expect the field.</param>
internal sealed record ChatCompletionChunkChoice(
    int Index,
    ChatCompletionMessage Delta,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FinishReason);

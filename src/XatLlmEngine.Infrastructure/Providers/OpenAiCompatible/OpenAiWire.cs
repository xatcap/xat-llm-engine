namespace XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

/// <summary>The body of a chat completions request.</summary>
/// <param name="Model">The model name.</param>
/// <param name="Messages">The conversation.</param>
/// <param name="Temperature">Sampling temperature.</param>
/// <param name="TopP">Nucleus sampling value.</param>
/// <param name="MaxTokens">Token limit, for servers that use the older field.</param>
/// <param name="MaxCompletionTokens">Token limit, for servers that use the newer field.</param>
/// <param name="Stop">Stop sequences.</param>
/// <param name="Stream">Whether to stream the answer.</param>
/// <param name="StreamOptions">Streaming settings.</param>
internal sealed record OpenAiChatRequest(
    string Model,
    IReadOnlyList<OpenAiMessage> Messages,
    double? Temperature,
    double? TopP,
    int? MaxTokens,
    int? MaxCompletionTokens,
    IReadOnlyList<string>? Stop,
    bool? Stream,
    OpenAiStreamOptions? StreamOptions);

/// <summary>A chat message, also used for the delta inside a stream chunk.</summary>
/// <param name="Role">The author's role.</param>
/// <param name="Content">The text.</param>
internal sealed record OpenAiMessage(string? Role, string? Content);

/// <summary>Streaming settings.</summary>
/// <param name="IncludeUsage">Whether to send token counts at the end of the stream.</param>
internal sealed record OpenAiStreamOptions(bool IncludeUsage);

/// <summary>A chat completions response or a stream chunk, which share this shape.</summary>
/// <param name="Choices">The answers. The engine always asks for one.</param>
/// <param name="Usage">Token counts.</param>
/// <param name="Error">An error reported inside a stream.</param>
internal sealed record OpenAiChatResponse(IReadOnlyList<OpenAiChoice>? Choices, OpenAiUsage? Usage, UpstreamErrorBody? Error);

/// <summary>One answer.</summary>
/// <param name="Message">The complete message of a non-streaming response.</param>
/// <param name="Delta">The new text of a stream chunk.</param>
/// <param name="FinishReason">Why generation stopped.</param>
internal sealed record OpenAiChoice(OpenAiMessage? Message, OpenAiMessage? Delta, string? FinishReason);

/// <summary>Token counts.</summary>
/// <param name="PromptTokens">Tokens in the prompt.</param>
/// <param name="CompletionTokens">Tokens generated.</param>
internal sealed record OpenAiUsage(int PromptTokens, int CompletionTokens);

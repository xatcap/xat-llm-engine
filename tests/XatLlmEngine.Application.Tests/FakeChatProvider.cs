using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Tests;

/// <summary>An in-memory provider that records what it receives.</summary>
/// <param name="name">The provider's name.</param>
internal sealed class FakeChatProvider(string name) : IChatProvider
{
    /// <inheritdoc/>
    public string Name => name;

    /// <inheritdoc/>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <inheritdoc/>
    public IReadOnlyList<string> RequestFields { get; init; } = ["messages"];

    /// <inheritdoc/>
    public string? InitialSystemPrompt { get; init; }

    /// <inheritdoc/>
    public int MaxSystemPromptLength { get; init; } = 10_000;

    /// <summary>Gets the failure to return when the router applies the system prompt, if any.</summary>
    public EngineError? SystemPromptFailure { get; init; }

    /// <inheritdoc/>
    public Result<JsonObject> ApplySystemPrompt(JsonObject body, string? prompt)
    {
        if (SystemPromptFailure is not null)
        {
            return Result<JsonObject>.Fail(SystemPromptFailure);
        }

        if (prompt is null)
        {
            return Result<JsonObject>.Ok(body);
        }

        var result = body.DeepClone().AsObject();
        result["stored_prompt"] = prompt;
        return Result<JsonObject>.Ok(result);
    }

    /// <summary>Gets the most recent request.</summary>
    public ChatRequest? LastRequest { get; private set; }

    /// <summary>Gets the failure to return instead of an answer, if any.</summary>
    public EngineError? Failure { get; init; }

    /// <inheritdoc/>
    public Task<Result<ChatCompletion>> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(Failure is null
            ? Result<ChatCompletion>.Ok(new ChatCompletion("answer", FinishReason.Stop, null))
            : Result<ChatCompletion>.Fail(Failure));
    }

    /// <inheritdoc/>
    public Task<Result<IAsyncEnumerable<ChatChunk>>> StreamAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(Failure is null
            ? Result<IAsyncEnumerable<ChatChunk>>.Ok(new[] { new ChatChunk("answer", FinishReason.Stop, null) }.ToAsyncEnumerable())
            : Result<IAsyncEnumerable<ChatChunk>>.Fail(Failure));
    }
}

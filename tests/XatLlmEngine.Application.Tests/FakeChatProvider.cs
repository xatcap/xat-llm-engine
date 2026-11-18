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
    public IReadOnlyList<string> TokenLimitFields { get; init; } = ["max_tokens", "max_completion_tokens"];

    /// <inheritdoc/>
    public ProviderStatus Status { get; init; } = ProviderStatus.Ready;

    /// <inheritdoc/>
    public int MaxTokensLimit { get; init; }

    /// <summary>Gets a value that tells whether the provider tells the stream to forward token counts to the caller.</summary>
    public bool ForwardStreamUsage { get; init; } = true;

    /// <summary>Gets the most recent body passed to <see cref="ShouldForwardStreamUsage"/>.</summary>
    public JsonObject? LastStreamUsageBody { get; private set; }

    /// <summary>Gets the most recent request.</summary>
    public ChatRequest? LastRequest { get; private set; }

    /// <summary>Gets the failure to return instead of an answer, if any.</summary>
    public EngineError? Failure { get; init; }

    /// <summary>Gets the text of a complete answer.</summary>
    public string Answer { get; init; } = "answer";

    /// <summary>Gets the reason why a complete answer ended.</summary>
    public FinishReason Finish { get; init; } = FinishReason.Stop;

    /// <summary>Gets the token counts of a complete answer.</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>Gets the pieces of a stream.</summary>
    public IReadOnlyList<ChatChunk> Chunks { get; init; } = [new ChatChunk("answer", FinishReason.Stop, null)];

    /// <summary>Gets the failure to throw after the stream pieces, if any.</summary>
    public EngineError? StreamFailure { get; init; }

    /// <summary>Gets a value that tells whether the provider adds a field to the body it receives.</summary>
    public bool ChangeBody { get; init; }

    /// <inheritdoc/>
    public bool ShouldForwardStreamUsage(JsonObject body)
    {
        LastStreamUsageBody = body;
        return ForwardStreamUsage;
    }

    /// <inheritdoc/>
    public Task<Result<ChatCompletion>> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (ChangeBody)
        {
            request.Body["added"] = true;
        }

        return Task.FromResult(Failure is null
            ? Result<ChatCompletion>.Ok(new ChatCompletion(Answer, Finish, Usage))
            : Result<ChatCompletion>.Fail(Failure));
    }

    /// <inheritdoc/>
    public Task<Result<IAsyncEnumerable<ChatChunk>>> StreamAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(Failure is null
            ? Result<IAsyncEnumerable<ChatChunk>>.Ok(ReadAsync())
            : Result<IAsyncEnumerable<ChatChunk>>.Fail(Failure));
    }

    /// <summary>Yields the pieces, then throws the stream failure, if any.</summary>
    /// <returns>The pieces.</returns>
    private async IAsyncEnumerable<ChatChunk> ReadAsync()
    {
        foreach (var chunk in Chunks)
        {
            await Task.Yield();
            yield return chunk;
        }

        if (StreamFailure is not null)
        {
            throw new ChatStreamException(StreamFailure);
        }
    }
}

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

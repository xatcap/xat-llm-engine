using System.Runtime.CompilerServices;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Host.Tests;

/// <summary>A provider that gives the answers that each test chooses.</summary>
internal sealed class FakeProvider : IChatProvider
{
    /// <inheritdoc/>
    public string Name => "fake";

    /// <inheritdoc/>
    public IReadOnlyList<string> Models { get; } = ["model-a"];

    /// <inheritdoc/>
    public IReadOnlyList<string> RequestFields { get; } = ["messages", "temperature", "stream_options"];

    /// <inheritdoc/>
    public TokenBudget? Budget { get; set; }

    /// <summary>Gets or sets the chunks to stream.</summary>
    public IReadOnlyList<ChatChunk> Chunks { get; set; } = [];

    /// <summary>Gets or sets the failure to throw after the chunks.</summary>
    public ChatStreamException? ThrowAfterChunks { get; set; }

    /// <summary>Gets or sets the failure to return instead of an answer.</summary>
    public EngineError? Failure { get; set; }

    /// <summary>Gets or sets an unexpected failure to throw, which simulates a bug.</summary>
    public Exception? Crash { get; set; }

    /// <summary>Gets the most recent request.</summary>
    public ChatRequest? LastRequest { get; private set; }

    /// <summary>Gets or sets a task that the stream waits for before its first chunk, or <see langword="null"/> for no wait.</summary>
    public TaskCompletionSource? FirstChunkGate { get; set; }

    /// <summary>Gets a task that completes when a stream starts to read.</summary>
    public TaskCompletionSource StreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc/>
    public Task<Result<ChatCompletion>> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        if (Crash is not null)
        {
            throw Crash;
        }

        LastRequest = request;
        return Task.FromResult(Failure is null
            ? Result<ChatCompletion>.Ok(new ChatCompletion("Hello!", FinishReason.Stop, new TokenUsage(3, 2)))
            : Result<ChatCompletion>.Fail(Failure));
    }

    /// <inheritdoc/>
    public Task<Result<IAsyncEnumerable<ChatChunk>>> StreamAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(Failure is null
            ? Result<IAsyncEnumerable<ChatChunk>>.Ok(ReadAsync(cancellationToken))
            : Result<IAsyncEnumerable<ChatChunk>>.Fail(Failure));
    }

    /// <summary>Yields the chunks, then any failure.</summary>
    /// <param name="cancellationToken">Stops reading.</param>
    /// <returns>The chunks.</returns>
    private async IAsyncEnumerable<ChatChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        StreamStarted.TrySetResult();
        if (FirstChunkGate is not null)
        {
            await FirstChunkGate.Task.WaitAsync(cancellationToken);
        }

        foreach (var chunk in Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
            await Task.Yield();
        }

        if (ThrowAfterChunks is not null)
        {
            throw ThrowAfterChunks;
        }
    }
}

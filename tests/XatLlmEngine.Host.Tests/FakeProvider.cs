using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
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
    public string? InitialSystemPrompt => null;

    /// <inheritdoc/>
    public int MaxSystemPromptLength => 40;

    /// <summary>Gets the stored prompt of the most recent request.</summary>
    public string? LastSystemPrompt { get; private set; }

    /// <inheritdoc/>
    public Result<JsonObject> ApplySystemPrompt(JsonObject body, string? prompt)
    {
        LastSystemPrompt = prompt;
        return Result<JsonObject>.Ok(body);
    }

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

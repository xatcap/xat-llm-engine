using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Diagnostics;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>A streamed answer that has started. The first piece has arrived, so the caller can send the success status.</summary>
/// <remarks>
/// This class holds the rules of a stream: the answer must end with a finish reason, and the token counts arrive in any piece.
/// The caller must dispose it, so that the provider's connection returns to the pool. See ADR 0008.
/// </remarks>
public sealed class StreamedAnswer : IAsyncDisposable
{
    /// <summary>The provider's pieces. The first piece is in <see cref="first"/>.</summary>
    private readonly IAsyncEnumerator<ChatChunk> source;

    /// <summary>The first piece, which the handler read before it returned.</summary>
    private readonly ChatChunk? first;

    /// <summary>Records the end of the request. The stream records it, because the request ends with the stream.</summary>
    private readonly EngineTelemetry.ChatMeasurement measurement;

    /// <summary>Tells whether the end of the request is recorded.</summary>
    private bool recorded;

    /// <summary>Tells whether the enumeration has started, because a stream can be read only once.</summary>
    private bool started;

    /// <summary>Initializes a new instance of the <see cref="StreamedAnswer"/> class.</summary>
    /// <param name="source">The provider's pieces, after the first piece.</param>
    /// <param name="first">The first piece.</param>
    /// <param name="model">The full <c>provider/model</c> reference.</param>
    /// <param name="forwardUsage">Whether the caller must get the token counts.</param>
    /// <param name="measurement">Records the end of the request.</param>
    internal StreamedAnswer(IAsyncEnumerator<ChatChunk> source, ChatChunk first, string model, bool forwardUsage, EngineTelemetry.ChatMeasurement measurement)
    {
        this.source = source;
        this.first = first;
        Model = model;
        ForwardUsage = forwardUsage;
        this.measurement = measurement;
    }

    /// <summary>Gets the full <c>provider/model</c> reference to report back to the caller.</summary>
    public string Model { get; }

    /// <summary>Gets the token counts that the provider reported. They are complete only after <see cref="ReadAsync"/> has ended.</summary>
    public TokenUsage? Usage { get; private set; }

    /// <summary>Gets a value indicating whether the caller must get the token counts, as one last piece of the stream.</summary>
    public bool ForwardUsage { get; }

    /// <summary>Reads the pieces that carry text or a finish reason. Token counts go to <see cref="Usage"/> instead.</summary>
    /// <returns>The pieces, in order.</returns>
    /// <exception cref="ChatStreamException">The provider failed, or the stream ended before the answer was complete.</exception>
    /// <exception cref="InvalidOperationException">The stream was read before.</exception>
    public async IAsyncEnumerable<ChatChunk> ReadAsync()
    {
        if (started)
        {
            throw new InvalidOperationException("A stream can be read only once.");
        }

        started = true;
        var finished = false;
        for (var chunk = first; chunk is not null; chunk = await NextAsync().ConfigureAwait(false))
        {
            Usage = chunk.Usage ?? Usage;
            if (chunk.Content is not null || chunk.FinishReason is not null)
            {
                finished |= chunk.FinishReason is not null;
                yield return chunk with { Usage = null };
            }
        }

        if (!finished)
        {
            Fail(new EngineError(ErrorKind.Upstream, "The provider ended the stream before the answer was complete."));
        }

        Record(failure: null);
    }

    /// <inheritdoc/>
    /// <remarks>A stream that did not end counts as canceled, because the caller stopped reading it.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (!recorded)
        {
            recorded = true;
            measurement.Cancel(Usage);
        }

        await source.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Records a failure, and throws it.</summary>
    /// <param name="error">The failure.</param>
    /// <exception cref="ChatStreamException">Always.</exception>
    private void Fail(EngineError error)
    {
        Record(error.Kind);
        throw new ChatStreamException(error);
    }

    /// <summary>Records the end of the request once.</summary>
    /// <param name="failure">The failure, or <see langword="null"/> for a success.</param>
    private void Record(ErrorKind? failure)
    {
        if (!recorded)
        {
            recorded = true;
            measurement.Finish(failure, Usage);
        }
    }

    /// <summary>Reads the next piece, and records a failure of the provider.</summary>
    /// <returns>The piece, or <see langword="null"/> at the end.</returns>
    private async ValueTask<ChatChunk?> NextAsync()
    {
        try
        {
            return await source.MoveNextAsync().ConfigureAwait(false) ? source.Current : null;
        }
        catch (ChatStreamException exception)
        {
            Record(exception.Error.Kind);
            throw;
        }
    }
}

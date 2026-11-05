using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Starts a streamed chat answer from the provider named by the caller.</summary>
public sealed class StreamChatHandler
{
    /// <summary>The shared validation and provider selection.</summary>
    private readonly ChatRouter router;

    /// <summary>Initializes a new instance of the <see cref="StreamChatHandler"/> class.</summary>
    /// <param name="providers">Every provider enabled in this deployment.</param>
    public StreamChatHandler(IEnumerable<IChatProvider> providers)
    {
        router = new ChatRouter(providers);
    }

    /// <summary>Validates the command and starts the stream.</summary>
    /// <param name="command">The caller's request.</param>
    /// <param name="cancellationToken">Cancels generation, for example when the caller disconnects.</param>
    /// <returns>
    /// The stream with its full model reference, or a failure that occurred before the first text.
    /// Enumerating the stream can throw <see cref="ChatStreamException"/>.
    /// </returns>
    public async Task<Result<(IAsyncEnumerable<ChatChunk> Chunks, string Model)>> HandleAsync(
        CompleteChatCommand command,
        CancellationToken cancellationToken)
    {
        var routed = router.Route(command);
        if (!routed.IsSuccess)
        {
            return Result<(IAsyncEnumerable<ChatChunk>, string)>.Fail(routed.Error);
        }

        var chat = routed.Value!;
        var stream = await chat.Provider.StreamAsync(chat.Request, cancellationToken).ConfigureAwait(false);
        return stream.IsSuccess
            ? Result<(IAsyncEnumerable<ChatChunk>, string)>.Ok((stream.Value!, chat.Model.ToString()))
            : Result<(IAsyncEnumerable<ChatChunk>, string)>.Fail(stream.Error);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Domain;
using XatLlmEngine.Host.Contracts;

namespace XatLlmEngine.Host.Endpoints;

/// <summary>Implements <c>POST /v1/chat/completions</c>.</summary>
internal static class ChatCompletionsEndpoint
{
    /// <summary>The object type of a complete answer.</summary>
    private const string CompletionObject = "chat.completion";

    /// <summary>The object type of a stream piece.</summary>
    private const string ChunkObject = "chat.completion.chunk";

    /// <summary>Generates a chat answer, in one piece or as a stream.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="completeHandler">The use case for complete answers.</param>
    /// <param name="streamHandler">The use case for streamed answers.</param>
    /// <param name="timeProvider">The clock for the creation time.</param>
    /// <returns>The HTTP result.</returns>
    public static async Task<IResult> HandleAsync(
        HttpContext context,
        CompleteChatHandler completeHandler,
        StreamChatHandler streamHandler,
        TimeProvider timeProvider)
    {
        var cancellationToken = context.RequestAborted;
        try
        {
            // JSON content is required because browsers cannot send it across sites without a CORS approval, which the engine never gives.
            if (!context.Request.HasJsonContentType())
            {
                return Results.Json(
                    new ErrorResponse(new ErrorDetails("The Content-Type must be application/json.", "invalid_request_error")),
                    HostJson.Default.ErrorResponse,
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }

            ChatCompletionRequest? request;
            try
            {
                request = await JsonSerializer
                    .DeserializeAsync(context.Request.Body, HostJson.Default.ChatCompletionRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return ErrorMapping.ToResult(context, new EngineError(ErrorKind.InvalidRequest, "The request body is not valid JSON for this API."));
            }

            if (request is null)
            {
                return ErrorMapping.ToResult(context, new EngineError(ErrorKind.InvalidRequest, "The request body is required."));
            }

            var command = ChatRequestMapper.ToCommand(request);
            if (!command.IsSuccess)
            {
                return ErrorMapping.ToResult(context, command.Error);
            }

            var id = $"chatcmpl-{Guid.NewGuid():N}";
            var created = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            return request.Stream == true
                ? await StreamAsync(context, streamHandler, command.Value!, request.StreamOptions?.IncludeUsage == true, id, created).ConfigureAwait(false)
                : await CompleteAsync(context, completeHandler, command.Value!, id, created).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller disconnected, so nobody is left to answer.
            return Results.Empty;
        }
    }

    /// <summary>Produces a complete answer.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="handler">The use case.</param>
    /// <param name="command">The validated-format command.</param>
    /// <param name="id">The answer's ID.</param>
    /// <param name="created">The creation time in Unix seconds.</param>
    /// <returns>The HTTP result.</returns>
    private static async Task<IResult> CompleteAsync(HttpContext context, CompleteChatHandler handler, CompleteChatCommand command, string id, long created)
    {
        var result = await handler.HandleAsync(command, context.RequestAborted).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return ErrorMapping.ToResult(context, result.Error);
        }

        var (completion, model) = result.Value;
        var response = new ChatCompletionResponse(
            id,
            CompletionObject,
            created,
            model,
            [new ChatCompletionChoice(0, new ChatCompletionMessage("assistant", completion.Content), ToWire(completion.FinishReason))],
            ToWire(completion.Usage));
        return Results.Json(response, HostJson.Default.ChatCompletionResponse);
    }

    /// <summary>Produces a streamed answer.</summary>
    /// <remarks>
    /// A failure before the first piece still returns a normal HTTP error, so the first piece is read before anything
    /// is written. That also starts the stream, which guarantees its connection is released even if writing fails.
    /// After that the status has been sent, so a failure becomes an error event, with no <c>[DONE]</c> marker.
    /// </remarks>
    /// <param name="context">The current request.</param>
    /// <param name="handler">The use case.</param>
    /// <param name="command">The validated-format command.</param>
    /// <param name="includeUsage">Whether the caller asked for token counts.</param>
    /// <param name="id">The answer's ID.</param>
    /// <param name="created">The creation time in Unix seconds.</param>
    /// <returns>The HTTP result.</returns>
    private static async Task<IResult> StreamAsync(HttpContext context, StreamChatHandler handler, CompleteChatCommand command, bool includeUsage, string id, long created)
    {
        var cancellationToken = context.RequestAborted;
        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return ErrorMapping.ToResult(context, result.Error);
        }

        var (chunks, model) = result.Value;
        var enumerator = chunks.GetAsyncEnumerator(cancellationToken);
        await using var ownedEnumerator = enumerator.ConfigureAwait(false);

        bool hasPiece;
        try
        {
            hasPiece = await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (ChatStreamException exception)
        {
            return ErrorMapping.ToResult(context, exception.Error);
        }

        if (!hasPiece)
        {
            return ErrorMapping.ToResult(context, new EngineError(ErrorKind.Upstream, "The provider ended the stream without an answer."));
        }

        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        ChatCompletionChunkResponse Piece(string? role, string? content, string? finishReason) => new(
            id,
            ChunkObject,
            created,
            model,
            [new ChatCompletionChunkChoice(0, new ChatCompletionMessage(role, content), finishReason)],
            null);

        try
        {
            await WriteEventAsync(response, Piece("assistant", string.Empty, null), HostJson.Default.ChatCompletionChunkResponse, cancellationToken).ConfigureAwait(false);

            TokenUsage? usage = null;
            var finished = false;
            for (; hasPiece; hasPiece = await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                var chunk = enumerator.Current;
                usage = chunk.Usage ?? usage;
                if (chunk.Content is not null || chunk.FinishReason is not null)
                {
                    finished |= chunk.FinishReason is not null;
                    var finishReason = chunk.FinishReason is { } reason ? ToWire(reason) : null;
                    await WriteEventAsync(response, Piece(null, chunk.Content, finishReason), HostJson.Default.ChatCompletionChunkResponse, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!finished)
            {
                throw new ChatStreamException(new EngineError(ErrorKind.Upstream, "The provider ended the stream before the answer was complete."));
            }

            if (includeUsage && usage is not null)
            {
                var usagePiece = new ChatCompletionChunkResponse(id, ChunkObject, created, model, [], ToWire(usage));
                await WriteEventAsync(response, usagePiece, HostJson.Default.ChatCompletionChunkResponse, cancellationToken).ConfigureAwait(false);
            }

            await response.WriteAsync("data: [DONE]\n\n", cancellationToken).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChatStreamException exception)
        {
            await WriteEventAsync(response, ErrorMapping.ToBody(exception.Error), HostJson.Default.ErrorResponse, cancellationToken).ConfigureAwait(false);
        }

        return Results.Empty;
    }

    /// <summary>Writes and immediately sends one event, so that callers see text as soon as it exists.</summary>
    /// <typeparam name="T">The event's body type.</typeparam>
    /// <param name="response">The HTTP response.</param>
    /// <param name="value">The event's body.</param>
    /// <param name="typeInfo">The JSON metadata for the body.</param>
    /// <param name="cancellationToken">Stops writing when the caller disconnects.</param>
    /// <returns>A task that completes when the event has been sent.</returns>
    private static async Task WriteEventAsync<T>(HttpResponse response, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        await response.WriteAsync($"data: {JsonSerializer.Serialize(value, typeInfo)}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Converts a finish reason to its OpenAI value.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The wire value.</returns>
    private static string ToWire(FinishReason reason) => reason switch
    {
        FinishReason.Length => "length",
        FinishReason.ContentFilter => "content_filter",
        _ => "stop",
    };

    /// <summary>Converts token counts to the OpenAI format.</summary>
    /// <param name="usage">The counts.</param>
    /// <returns>The wire value, or <see langword="null"/> when there are no counts.</returns>
    private static ChatCompletionUsage? ToWire(TokenUsage? usage) =>
        usage is null ? null : new ChatCompletionUsage(usage.InputTokens, usage.OutputTokens, usage.TotalTokens);
}

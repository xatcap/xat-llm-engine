using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Domain;
using XatLlmEngine.Host.Contracts;
using XatLlmEngine.Host.Diagnostics;

namespace XatLlmEngine.Host.Endpoints;

/// <summary>Implements <c>POST /v1/chat/completions</c>.</summary>
internal static class ChatCompletionsEndpoint
{
    /// <summary>The object type of a complete answer.</summary>
    private const string CompletionObject = "chat.completion";

    /// <summary>The object type of a stream piece.</summary>
    private const string ChunkObject = "chat.completion.chunk";

    /// <summary>The time between keep-alive comments while a started stream waits for its first piece.</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>Generates a chat answer, in one piece or as a stream.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="completeHandler">The use case for complete answers.</param>
    /// <param name="streamHandler">The use case for streamed answers.</param>
    /// <param name="timeProvider">The clock for the creation time.</param>
    /// <param name="apiOptions">The API settings, for the body size limit.</param>
    /// <param name="loggerFactory">Creates the log of finished requests.</param>
    /// <returns>The HTTP result.</returns>
    public static async Task<IResult> HandleAsync(
        HttpContext context,
        CompleteChatHandler completeHandler,
        StreamChatHandler streamHandler,
        TimeProvider timeProvider,
        IOptions<ApiOptions> apiOptions,
        ILoggerFactory loggerFactory)
    {
        var cancellationToken = context.RequestAborted;
        try
        {
            // The engine requires JSON content because browsers cannot send it across sites without a CORS approval, which the engine never gives.
            if (!context.Request.HasJsonContentType())
            {
                return ErrorMapping.UnsupportedMediaType();
            }

            var limit = apiOptions.Value.MaxRequestBodyBytes;
            var content = await ReadBodyAsync(context.Request, limit, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                return ErrorMapping.RequestTooLarge(limit);
            }

            JsonObject? body;
            try
            {
                var node = JsonNode.Parse(content.AsSpan());
                body = node as JsonObject;

                // The JSON parser checks JSON only at first use. Without this check, a problem causes a later 500 error, possibly after the provider call.
                // ReadAll finds a repeated field name. ToJsonString finds a string that is not valid UTF-16, for example a lone "\ud83d".
                ReadAll(body);
                _ = body?.ToJsonString();
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            {
                return ErrorMapping.ToResult(context, new EngineError(ErrorKind.InvalidRequest, "The request body is not valid JSON."));
            }

            if (body is null)
            {
                return ErrorMapping.ToResult(context, new EngineError(ErrorKind.InvalidRequest, "The request body must be a JSON object."));
            }

            var isStream = false;
            if (body["stream"] is { } stream && !(stream is JsonValue streamValue && streamValue.TryGetValue(out isStream)))
            {
                return ErrorMapping.ToResult(context, new EngineError(ErrorKind.InvalidRequest, "The 'stream' field must be true or false."));
            }

            var model = body["model"] is JsonValue modelValue && modelValue.TryGetValue<string>(out var name) ? name : null;
            var command = new CompleteChatCommand(model, body);
            var id = $"chatcmpl-{Guid.NewGuid():N}";
            var created = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var log = new RequestLog(loggerFactory.CreateLogger(ChatRequestLog.Category), model, isStream, timeProvider);
            using (log.Logger.BeginScope(new Dictionary<string, object> { ["CompletionId"] = id }))
            {
                return isStream
                    ? await StreamAsync(context, streamHandler, command, id, created, log, timeProvider, apiOptions.Value).ConfigureAwait(false)
                    : await CompleteAsync(context, completeHandler, command, id, created, log).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller disconnected, so nobody is left to answer.
            return Results.Empty;
        }
    }

    /// <summary>Reads the request body, up to a limit. The body is read in full, so that the JSON parser can check all of it.</summary>
    /// <param name="request">The request.</param>
    /// <param name="limit">The largest body to accept, in bytes.</param>
    /// <param name="cancellationToken">Stops reading when the caller disconnects.</param>
    /// <returns>The body, or <see langword="null"/> when it is larger than the limit.</returns>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, long limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var block = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(block, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(block, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Opens every nested object and array, which fails on a repeated field name.</summary>
    /// <param name="node">The JSON value.</param>
    private static void ReadAll(JsonNode? node)
    {
        if (node is JsonObject fields)
        {
            foreach (var field in fields)
            {
                ReadAll(field.Value);
            }
        }
        else if (node is JsonArray items)
        {
            foreach (var item in items)
            {
                ReadAll(item);
            }
        }
    }

    /// <summary>Produces a complete answer.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="handler">The use case.</param>
    /// <param name="command">The caller's request.</param>
    /// <param name="id">The answer's ID.</param>
    /// <param name="created">The creation time in Unix seconds.</param>
    /// <param name="log">The log of the finished request.</param>
    /// <returns>The HTTP result.</returns>
    private static async Task<IResult> CompleteAsync(HttpContext context, CompleteChatHandler handler, CompleteChatCommand command, string id, long created, RequestLog log)
    {
        var result = await handler.HandleAsync(command, context.RequestAborted).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            log.Failed(result.Error);
            return ErrorMapping.ToResult(context, result.Error);
        }

        var (completion, model) = result.Value;
        log.Finished(StatusCodes.Status200OK, "ok", completion.Usage);
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
    /// The handler reads the first piece before it returns, so a failure before the first piece still returns a normal HTTP error.
    /// When the first piece takes longer than <see cref="ApiOptions.StreamCommitSeconds"/>, the engine sends status 200 and keep-alive
    /// comments. After the caller has the status, a failure becomes an error event, with no <c>[DONE]</c> marker. See ADR 0008.
    /// </remarks>
    /// <param name="context">The current request.</param>
    /// <param name="handler">The use case.</param>
    /// <param name="command">The caller's request.</param>
    /// <param name="id">The answer's ID.</param>
    /// <param name="created">The creation time in Unix seconds.</param>
    /// <param name="log">The log of the finished request.</param>
    /// <param name="timeProvider">The clock, for the keep-alive comments.</param>
    /// <param name="options">The API settings.</param>
    /// <returns>The HTTP result.</returns>
    private static async Task<IResult> StreamAsync(
        HttpContext context,
        StreamChatHandler handler,
        CompleteChatCommand command,
        string id,
        long created,
        RequestLog log,
        TimeProvider timeProvider,
        ApiOptions options)
    {
        var cancellationToken = context.RequestAborted;
        var response = context.Response;
        var pending = handler.HandleAsync(command, cancellationToken);
        bool committed;
        try
        {
            committed = await KeepAliveUntilAsync(pending, response, options, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The caller left, or a write failed. A stream that starts later must still free its slot and its connection.
            await DisposeWhenStartedAsync(pending).ConfigureAwait(false);
            throw;
        }

        var result = await pending.ConfigureAwait(false);
        if (!result.IsSuccess && !committed)
        {
            log.Failed(result.Error);
            return ErrorMapping.ToResult(context, result.Error);
        }

        if (!result.IsSuccess)
        {
            log.Finished(StatusCodes.Status200OK, "error_event", usage: null);
            await WriteEventAsync(response, ErrorMapping.ToBody(result.Error), HostJson.Default.ErrorResponse, cancellationToken).ConfigureAwait(false);
            return Results.Empty;
        }

        await using var answer = result.Value.ConfigureAwait(false);
        var model = result.Value.Model;
        StartEventStream(response);

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
            await foreach (var chunk in result.Value.ReadAsync().ConfigureAwait(false))
            {
                var finishReason = chunk.FinishReason is { } reason ? ToWire(reason) : null;
                await WriteEventAsync(response, Piece(null, chunk.Content, finishReason), HostJson.Default.ChatCompletionChunkResponse, cancellationToken).ConfigureAwait(false);
            }

            if (result.Value.ForwardUsage && result.Value.Usage is { } usage)
            {
                var usagePiece = new ChatCompletionChunkResponse(id, ChunkObject, created, model, [], ToWire(usage));
                await WriteEventAsync(response, usagePiece, HostJson.Default.ChatCompletionChunkResponse, cancellationToken).ConfigureAwait(false);
            }

            await response.WriteAsync("data: [DONE]\n\n", cancellationToken).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            log.Finished(StatusCodes.Status200OK, "ok", result.Value.Usage);
        }
        catch (ChatStreamException exception)
        {
            log.Finished(StatusCodes.Status200OK, "error_event", result.Value.Usage);
            await WriteEventAsync(response, ErrorMapping.ToBody(exception.Error), HostJson.Default.ErrorResponse, cancellationToken).ConfigureAwait(false);
        }

        return Results.Empty;
    }

    /// <summary>Waits for the first piece. When it takes longer than the commit time, this method sends status 200 and keep-alive comments until it arrives.</summary>
    /// <param name="pending">The handler's work, which ends after the first piece or a failure.</param>
    /// <param name="response">The HTTP response.</param>
    /// <param name="options">The API settings.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="cancellationToken">Stops when the caller disconnects.</param>
    /// <returns><see langword="true"/> when the engine has sent status 200.</returns>
    private static async Task<bool> KeepAliveUntilAsync<T>(Task<T> pending, HttpResponse response, ApiOptions options, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (options.StreamCommitSeconds <= 0 || pending.IsCompleted)
        {
            return false;
        }

        var wait = TimeSpan.FromSeconds(options.StreamCommitSeconds);
        using var stopTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (await Task.WhenAny(pending, Task.Delay(wait, timeProvider, stopTimer.Token)).ConfigureAwait(false) != pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartEventStream(response);

            // A line that starts with ':' is a comment. Callers ignore it, but it resets their read time limit.
            await response.WriteAsync(": keep-alive\n\n", cancellationToken).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            wait = KeepAliveInterval;
        }

        // The last timer must not run on after the first piece.
        await stopTimer.CancelAsync().ConfigureAwait(false);
        return response.HasStarted;
    }

    /// <summary>Waits for a stream that nobody reads, and disposes it, so that its slot and its connection are free again.</summary>
    /// <param name="pending">The handler's work.</param>
    /// <returns>A task that completes when the stream is disposed.</returns>
    private static async Task DisposeWhenStartedAsync(Task<Result<StreamedAnswer>> pending)
    {
        try
        {
            var result = await pending.ConfigureAwait(false);
            if (result.IsSuccess)
            {
                await result.Value.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChatStreamException)
        {
            // The handler stopped with the caller. It has disposed what it started.
        }
    }

    /// <summary>Sets the headers of an event stream, once, before the first write.</summary>
    /// <param name="response">The HTTP response.</param>
    private static void StartEventStream(HttpResponse response)
    {
        if (!response.HasStarted)
        {
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
        }
    }

    /// <summary>Writes and immediately sends one event, so that callers see text as soon as it exists.</summary>
    /// <typeparam name="T">The event's body type.</typeparam>
    /// <param name="response">The HTTP response.</param>
    /// <param name="value">The event's body.</param>
    /// <param name="typeInfo">The JSON metadata for the body.</param>
    /// <param name="cancellationToken">Stops writing when the caller disconnects.</param>
    /// <returns>A task that completes after the engine sends the event.</returns>
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
        FinishReason.Stop => "stop",
    };

    /// <summary>Converts token counts to the OpenAI format.</summary>
    /// <param name="usage">The counts.</param>
    /// <returns>The wire value, or <see langword="null"/> when there are no counts.</returns>
    private static ChatCompletionUsage? ToWire(TokenUsage? usage) =>
        usage is null ? null : new ChatCompletionUsage(usage.InputTokens, usage.OutputTokens, usage.TotalTokens);

    /// <summary>Writes the one log line of a finished chat request.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="model">The model, as the caller wrote it.</param>
    /// <param name="stream">Whether the request asks for a stream.</param>
    /// <param name="timeProvider">The clock.</param>
    private sealed class RequestLog(ILogger logger, string? model, bool stream, TimeProvider timeProvider)
    {
        /// <summary>The start time.</summary>
        private readonly long started = timeProvider.GetTimestamp();

        /// <summary>Gets the log.</summary>
        public ILogger Logger => logger;

        /// <summary>Logs a request that failed before an answer started.</summary>
        /// <param name="error">The failure.</param>
        public void Failed(EngineError error)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                Finished(ErrorMapping.ToStatusCode(error.Kind), ErrorMapping.ToBody(error).Error.Type, usage: null);
            }
        }

        /// <summary>Logs a finished request.</summary>
        /// <param name="status">The HTTP status.</param>
        /// <param name="outcome">How the request ended.</param>
        /// <param name="usage">The token counts, if the provider reported them.</param>
        public void Finished(int status, string outcome, TokenUsage? usage)
        {
            var milliseconds = (long)timeProvider.GetElapsedTime(started).TotalMilliseconds;
            ChatRequestLog.Finished(logger, model, stream, status, outcome, milliseconds, usage?.InputTokens, usage?.OutputTokens);
        }
    }
}

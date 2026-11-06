using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>Tells whether a provider runs on this machine under the engine's control, or is a cloud service.</summary>
internal enum ProviderKind
{
    /// <summary>A cloud service, with a key from the settings.</summary>
    Cloud,

    /// <summary>The supervised llama-server, with a random key for each run.</summary>
    Local,
}

/// <summary>The settings of one provider's transport.</summary>
/// <param name="ProviderName">The provider's name, for messages and logs.</param>
/// <param name="Kind">The kind of provider.</param>
/// <param name="HttpClientName">The named HTTP client.</param>
/// <param name="RequestTimeout">How long to wait for the response headers, or <see langword="null"/> for no limit.</param>
/// <param name="ReadIdleTimeout">How long one read of the response body can wait for data, or <see langword="null"/> for no limit.</param>
internal sealed record TransportSettings(string ProviderName, ProviderKind Kind, string HttpClientName, TimeSpan? RequestTimeout, TimeSpan? ReadIdleTimeout);

/// <summary>Sends requests to a provider and translates every transport failure into an engine error. Both protocols use this class.</summary>
/// <remarks>The engine never retries a request. See ADR 0006.</remarks>
/// <param name="settings">The transport settings.</param>
/// <param name="httpClientFactory">Creates HTTP clients, so that the connection pool works correctly.</param>
/// <param name="timeProvider">The clock, for the time limits and for a Retry-After header that holds a date.</param>
/// <param name="logger">The log for failures.</param>
internal sealed partial class ProviderTransport(
    TransportSettings settings,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger logger)
{
    /// <summary>The largest successful response body that the engine reads. A chat answer is much smaller.</summary>
    internal const long MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>The largest error body that the engine reads. The engine needs only the message from it.</summary>
    internal const int MaxErrorBodyBytes = 64 * 1024;

    /// <summary>Gets the provider's name.</summary>
    public string ProviderName => settings.ProviderName;

    /// <summary>Sends a request for a complete answer, and reads the JSON answer.</summary>
    /// <typeparam name="TBody">The wire type of the answer.</typeparam>
    /// <typeparam name="TResult">The engine type of the answer.</typeparam>
    /// <param name="address">The full address of the operation.</param>
    /// <param name="authenticate">Adds the provider's authentication headers.</param>
    /// <param name="body">The request body.</param>
    /// <param name="bodyType">The source-generated JSON contract of the answer.</param>
    /// <param name="map">Converts the wire answer. It returns a failure when the answer is not complete.</param>
    /// <param name="cancellationToken">Cancels the request. The caller's cancellation is not converted into an error.</param>
    /// <returns>The answer, or the expected failure.</returns>
    public async Task<Result<TResult>> CompleteAsync<TBody, TResult>(
        Uri address,
        Action<HttpRequestHeaders> authenticate,
        JsonObject body,
        JsonTypeInfo<TBody> bodyType,
        Func<TBody?, Result<TResult>> map,
        CancellationToken cancellationToken)
        where TResult : notnull
    {
        try
        {
            using var response = await SendAsync(address, authenticate, body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<TResult>.Fail(await FailAsync(response, cancellationToken).ConfigureAwait(false));
            }

            var content = await OpenBodyAsync(response, cancellationToken).ConfigureAwait(false);
            await using (content.ConfigureAwait(false))
            {
                var limited = new SizeLimitedStream(content, MaxResponseBytes);
                await using (limited.ConfigureAwait(false))
                {
                    return map(await JsonSerializer.DeserializeAsync(limited, bodyType, cancellationToken).ConfigureAwait(false));
                }
            }
        }
        catch (Exception exception) when (TryTranslate(exception, cancellationToken, out var error))
        {
            return Result<TResult>.Fail(error);
        }
    }

    /// <summary>Sends a request for a stream, and returns as soon as the response headers arrive.</summary>
    /// <param name="address">The full address of the operation.</param>
    /// <param name="authenticate">Adds the provider's authentication headers.</param>
    /// <param name="body">The request body.</param>
    /// <param name="read">Converts the server-sent events into chunks.</param>
    /// <param name="cancellationToken">Cancels the request and the stream.</param>
    /// <returns>The chunks, or a failure when the stream could not start.</returns>
    public async Task<Result<IAsyncEnumerable<ChatChunk>>> StreamAsync(
        Uri address,
        Action<HttpRequestHeaders> authenticate,
        JsonObject body,
        Func<IAsyncEnumerable<SseItem<string>>, IAsyncEnumerable<ChatChunk>> read,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(address, authenticate, body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                using (response)
                {
                    return Result<IAsyncEnumerable<ChatChunk>>.Fail(await FailAsync(response, cancellationToken).ConfigureAwait(false));
                }
            }

            return Result<IAsyncEnumerable<ChatChunk>>.Ok(read(ReadEventsAsync(response, cancellationToken)));
        }
        catch (Exception exception) when (TryTranslate(exception, cancellationToken, out var error))
        {
            response?.Dispose();
            return Result<IAsyncEnumerable<ChatChunk>>.Fail(error);
        }
    }

    /// <summary>Builds the error for an event in which the provider reports a failure.</summary>
    /// <returns>An error that is safe to show to the caller.</returns>
    public EngineError StreamFailed() =>
        new(ErrorKind.Upstream, $"The provider '{settings.ProviderName}' failed during the stream.");

    /// <summary>Builds the error for a response the engine could not understand.</summary>
    /// <returns>An error that is safe to show to the caller.</returns>
    public EngineError Malformed() => HttpFailure.Malformed(settings.ProviderName);

    /// <summary>Sends the request and returns as soon as response headers arrive, so that streams are not buffered.</summary>
    /// <param name="address">The full address.</param>
    /// <param name="authenticate">Adds the authentication headers.</param>
    /// <param name="body">The request body.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, which the caller must dispose.</returns>
    /// <exception cref="ProviderTimeoutException">The headers did not arrive in time.</exception>
    private async Task<HttpResponseMessage> SendAsync(Uri address, Action<HttpRequestHeaders> authenticate, JsonObject body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, address)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        authenticate(message.Headers);

        using var client = httpClientFactory.CreateClient(settings.HttpClientName);
        if (settings.RequestTimeout is not { } timeout)
        {
            return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }

        // The limit covers only the wait for the headers. The idle limit covers the body, so a long stream can continue.
        using var timer = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ProviderTimeoutException(timeout, exception);
        }
    }

    /// <summary>Opens the response body, with the idle limit when the settings have one.</summary>
    /// <param name="response">The response.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The body stream, which the caller must dispose.</returns>
    private async Task<Stream> OpenBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return settings.ReadIdleTimeout is { } idle ? new IdleTimeoutStream(stream, idle, timeProvider) : stream;
    }

    /// <summary>Builds the error for a failed response. A body that the engine cannot read gives the general message for the status.</summary>
    /// <param name="response">The failed response.</param>
    /// <param name="cancellationToken">Cancels reading the body.</param>
    /// <returns>An error that is safe to show to the caller.</returns>
    private async Task<EngineError> FailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? body = null;
        try
        {
            body = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or TimeoutException or InvalidDataException)
        {
            // The status alone is enough for the error.
        }

        return HttpFailure.FromResponse(settings.ProviderName, settings.Kind, response, body, timeProvider.GetUtcNow(), logger);
    }

    /// <summary>Reads at most <see cref="MaxErrorBodyBytes"/> of an error body.</summary>
    /// <param name="response">The failed response.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The body text, or <see langword="null"/> when it is larger than the limit.</returns>
    private async Task<string?> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await OpenBodyAsync(response, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[MaxErrorBodyBytes + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                length += read;
            }

            return length > MaxErrorBodyBytes ? null : Encoding.UTF8.GetString(buffer, 0, length);
        }
    }

    /// <summary>Yields each server-sent event, and disposes the response when the enumeration ends.</summary>
    /// <param name="response">A successful response, which this method takes ownership of.</param>
    /// <param name="cancellationToken">Cancels reading, which also ends generation upstream.</param>
    /// <returns>The events in the order they arrive.</returns>
    /// <exception cref="ChatStreamException">The connection failed or stalled part-way through the stream.</exception>
    private async IAsyncEnumerable<SseItem<string>> ReadEventsAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (response)
        {
            var stream = await OpenBodyAsync(response, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var events = SseParser
                    .Create(stream, static (_, data) => Encoding.UTF8.GetString(data))
                    .EnumerateAsync(cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                await using (events.ConfigureAwait(false))
                {
                    while (await MoveNextAsync(events, cancellationToken).ConfigureAwait(false))
                    {
                        yield return events.Current;
                    }
                }
            }
        }
    }

    /// <summary>Advances the stream and translates transport failures. It is separate because C# forbids <c>yield</c> inside a <c>try</c> with a <c>catch</c>.</summary>
    /// <param name="events">The event enumerator.</param>
    /// <param name="cancellationToken">The caller's cancellation, which is not converted into an error.</param>
    /// <returns><see langword="true"/> when another event is available.</returns>
    private async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<SseItem<string>> events, CancellationToken cancellationToken)
    {
        try
        {
            return await events.MoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (TryTranslate(exception, cancellationToken, out var error))
        {
            throw new ChatStreamException(error, exception);
        }
    }

    /// <summary>Translates a transport failure into an engine error, and logs it. The caller's own cancellation stays an exception.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    /// <param name="error">The error, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the failure is an expected transport failure.</returns>
    private bool TryTranslate(Exception exception, CancellationToken cancellationToken, out EngineError error)
    {
        var name = settings.ProviderName;
        EngineError? translated = exception switch
        {
            _ when cancellationToken.IsCancellationRequested => null,
            ProviderTimeoutException timeout =>
                new(ErrorKind.Unavailable, $"The provider '{name}' did not start its answer within {timeout.Timeout.TotalSeconds:0} seconds."),
            ProviderStalledException stalled =>
                new(ErrorKind.Unavailable, $"The provider '{name}' sent no data for {stalled.Timeout.TotalSeconds:0} seconds."),
            HttpRequestException or IOException =>
                new(ErrorKind.Unavailable, $"The connection to the provider '{name}' failed."),
            JsonException or InvalidDataException => HttpFailure.Malformed(name),
            _ => null,
        };

        error = translated ?? default!;
        if (translated is null)
        {
            return false;
        }

        if (translated.Kind == ErrorKind.Unavailable)
        {
            LogTransportFailure(logger, exception, name);
        }

        return true;
    }

    /// <summary>Logs a connection failure or a time limit. The exception does not hold request headers or bodies.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="exception">The cause.</param>
    /// <param name="providerName">The provider's name.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "The request to the provider {ProviderName} failed before a complete answer arrived.")]
    private static partial void LogTransportFailure(ILogger logger, Exception exception, string providerName);
}

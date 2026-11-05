using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>Translates failed provider responses into engine errors (anti-corruption layer).</summary>
internal static partial class HttpFailure
{
    /// <summary>Anthropic's non-standard status for "the API is temporarily overloaded".</summary>
    private const int Overloaded = 529;

    /// <summary>The longest wait before one retry, including when a provider's Retry-After header asks for longer.</summary>
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Builds the error for a failed response.</summary>
    /// <remarks>
    /// The engine forwards the provider's own message only for problems that the caller can correct. Other messages stay hidden,
    /// because an authentication error from a provider can echo part of the engine's secret key.
    /// </remarks>
    /// <param name="providerName">The provider's name, for the message.</param>
    /// <param name="response">The failed response.</param>
    /// <param name="now">The current time, for a Retry-After header that holds a date.</param>
    /// <param name="logger">The log. It gets the provider's name and the status, never the body.</param>
    /// <param name="cancellationToken">Cancels reading the response body.</param>
    /// <returns>An error that is safe to show to the caller. For a temporary failure, it holds the provider's requested wait, if the provider sent one.</returns>
    public static async Task<EngineError> FromResponseAsync(
        string providerName,
        HttpResponseMessage response,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var error = response.StatusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity =>
                new EngineError(ErrorKind.InvalidRequest, await ReadMessageAsync(response, $"The provider '{providerName}' rejected the request.", cancellationToken).ConfigureAwait(false)),
            HttpStatusCode.NotFound =>
                new EngineError(ErrorKind.NotFound, await ReadMessageAsync(response, $"The provider '{providerName}' does not have this model.", cancellationToken).ConfigureAwait(false)),
            HttpStatusCode.TooManyRequests =>
                new EngineError(ErrorKind.RateLimited, $"The provider '{providerName}' applies a rate limit to requests.", RetryDelay(response, now)),
            HttpStatusCode.ServiceUnavailable or (HttpStatusCode)Overloaded =>
                new EngineError(ErrorKind.Unavailable, $"The provider '{providerName}' is temporarily unavailable.", RetryDelay(response, now)),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new EngineError(ErrorKind.Upstream, $"The provider '{providerName}' rejected the engine's credentials. Check its configured API key."),
            _ => new EngineError(ErrorKind.Upstream, $"The provider '{providerName}' returned status {status}."),
        };

        // The caller gets the message for an error that the caller can correct. Thus the log records that error at Debug level only.
        var level = error.Kind is ErrorKind.InvalidRequest or ErrorKind.NotFound ? LogLevel.Debug : LogLevel.Warning;
        LogFailedResponse(logger, level, providerName, status);
        return error;
    }

    /// <summary>Builds the error for a provider that the engine could not reach in time. This method also logs the failure.</summary>
    /// <param name="providerName">The provider's name, for the message.</param>
    /// <param name="exception">The cause: a network exception or a timeout exception. These exceptions do not hold request headers or bodies.</param>
    /// <param name="logger">The log.</param>
    /// <returns>An error that is safe to show to the caller.</returns>
    public static EngineError Unreachable(string providerName, Exception exception, ILogger logger)
    {
        LogUnreachable(logger, exception, providerName);
        return new(ErrorKind.Unavailable, $"The engine could not reach the provider '{providerName}' in time.");
    }

    /// <summary>Builds the error for a response the engine could not understand.</summary>
    /// <param name="providerName">The provider's name, for the message.</param>
    /// <returns>An error that is safe to show to the caller.</returns>
    public static EngineError Malformed(string providerName) =>
        new(ErrorKind.Upstream, $"The provider '{providerName}' returned a response that the engine could not read.");

    /// <summary>Obeys a provider's Retry-After header, up to the limit. MaxDelay does not limit this header, so without this limit a provider can hold a caller's request for many minutes.</summary>
    /// <param name="response">The response to retry, if there was one.</param>
    /// <param name="now">The current time, for a header that holds a date.</param>
    /// <returns>The delay to use, or <see langword="null"/> to use the normal backoff.</returns>
    internal static TimeSpan? RetryDelay(HttpResponseMessage? response, DateTimeOffset now)
    {
        var header = response?.Headers.RetryAfter;
        var requested = header?.Delta ?? (header?.Date - now);
        if (requested is not { } delay)
        {
            return null;
        }

        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay < MaximumRetryDelay ? delay : MaximumRetryDelay;
    }

    /// <summary>Reads the provider's error message, which any of the supported protocols may send.</summary>
    /// <param name="response">The failed response.</param>
    /// <param name="fallback">The text to use when the body has no readable message.</param>
    /// <param name="cancellationToken">Cancels reading the body.</param>
    /// <returns>The provider's message, or <paramref name="fallback"/>.</returns>
    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, string fallback, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var envelope = await JsonSerializer
                    .DeserializeAsync(body, ProviderJson.Default.UpstreamErrorEnvelope, cancellationToken)
                    .ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(envelope?.Error?.Message) ? fallback : envelope.Error.Message;
            }
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>Logs a failed provider response, so that an operator can find a bad API key or a provider that fails often.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="level">The log level.</param>
    /// <param name="providerName">The provider's name.</param>
    /// <param name="status">The HTTP status.</param>
    [LoggerMessage(Message = "The provider {ProviderName} returned status {Status}.")]
    private static partial void LogFailedResponse(ILogger logger, LogLevel level, string providerName, int status);

    /// <summary>Logs that the engine could not reach a provider in time.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="exception">The cause.</param>
    /// <param name="providerName">The provider's name.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "The engine could not reach the provider {ProviderName} in time.")]
    private static partial void LogUnreachable(ILogger logger, Exception exception, string providerName);
}

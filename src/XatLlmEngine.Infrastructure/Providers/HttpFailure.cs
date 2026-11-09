using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>Translates failed provider responses into engine errors (anti-corruption layer).</summary>
internal static partial class HttpFailure
{
    /// <summary>A non-standard status for "the API is temporarily overloaded".</summary>
    private const int Overloaded = 529;

    /// <summary>The longest Retry-After wait that the engine gives to the caller. Without this limit, a provider could ask callers to wait for hours.</summary>
    internal static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>Builds the error for a failed response.</summary>
    /// <remarks>
    /// The engine forwards the provider's own message only for problems that the caller can correct. Other messages stay hidden,
    /// because an authentication error from a provider can echo part of the engine's secret key.
    /// </remarks>
    /// <param name="providerName">The provider's name, for the message.</param>
    /// <param name="kind">The kind of provider. The advice for an authentication failure depends on it.</param>
    /// <param name="response">The failed response.</param>
    /// <param name="body">The response body, or <see langword="null"/> when the engine could not read it.</param>
    /// <param name="now">The current time, for a Retry-After header that holds a date.</param>
    /// <param name="logger">The log. It gets the provider's name and the status, never the body.</param>
    /// <returns>An error that is safe to show to the caller. For a temporary failure, it holds the provider's requested wait, if the provider sent one.</returns>
    public static EngineError FromResponse(string providerName, ProviderKind kind, HttpResponseMessage response, string? body, DateTimeOffset now, ILogger logger)
    {
        var status = (int)response.StatusCode;
        var error = response.StatusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity =>
                new EngineError(ErrorKind.InvalidRequest, ReadMessage(body) ?? $"The provider '{providerName}' rejected the request."),
            HttpStatusCode.NotFound =>
                new EngineError(ErrorKind.NotFound, ReadMessage(body) ?? $"The provider '{providerName}' does not have this model."),
            HttpStatusCode.TooManyRequests =>
                new EngineError(ErrorKind.RateLimited, $"The provider '{providerName}' applies a rate limit to requests.", RetryDelay(response, now)),
            HttpStatusCode.ServiceUnavailable or (HttpStatusCode)Overloaded =>
                new EngineError(ErrorKind.Unavailable, $"The provider '{providerName}' is temporarily unavailable.", RetryDelay(response, now)),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new EngineError(ErrorKind.Upstream, CredentialsMessage(providerName, kind)),
            _ => new EngineError(ErrorKind.Upstream, $"The provider '{providerName}' returned status {status}."),
        };

        // The caller gets the message for an error that the caller can correct. Thus the log records that error at Debug level only.
        var level = error.Kind is ErrorKind.InvalidRequest or ErrorKind.NotFound ? LogLevel.Debug : LogLevel.Warning;
        LogFailedResponse(logger, level, providerName, status);
        return error;
    }

    /// <summary>Builds the error for a response the engine could not understand.</summary>
    /// <param name="providerName">The provider's name, for the message.</param>
    /// <returns>An error that is safe to show to the caller.</returns>
    public static EngineError Malformed(string providerName) =>
        new(ErrorKind.Upstream, $"The provider '{providerName}' returned a response that the engine could not read.");

    /// <summary>Reads the wait that a provider's Retry-After header asks for, up to <see cref="MaximumRetryAfter"/>.</summary>
    /// <param name="response">The failed response, if there was one.</param>
    /// <param name="now">The current time, for a header that holds a date.</param>
    /// <returns>The wait, or <see langword="null"/> when the provider sent no header.</returns>
    internal static TimeSpan? RetryDelay(HttpResponseMessage? response, DateTimeOffset now)
    {
        var header = response?.Headers.RetryAfter;
        var requested = header?.Delta ?? (header?.Date - now);
        if (requested is not { } delay)
        {
            return null;
        }

        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay < MaximumRetryAfter ? delay : MaximumRetryAfter;
    }

    /// <summary>Gives the advice for an authentication failure.</summary>
    /// <param name="providerName">The provider's name.</param>
    /// <param name="kind">The kind of provider.</param>
    /// <returns>The message.</returns>
    private static string CredentialsMessage(string providerName, ProviderKind kind) => kind switch
    {
        // The engine makes a new key for each run of llama-server, so the user cannot correct a key. Another program can answer on the port.
        ProviderKind.Local => "llama-server refused the engine's key. Another program can use its port. Restart the engine.",
        ProviderKind.Cloud => $"The provider '{providerName}' rejected the engine's credentials. Check its configured API key.",
    };

    /// <summary>Reads the provider's error message, which any of the supported protocols may send.</summary>
    /// <param name="body">The response body.</param>
    /// <returns>The provider's message, or <see langword="null"/> when the body has no readable message.</returns>
    private static string? ReadMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var message = JsonSerializer.Deserialize(body, ProviderJson.Default.UpstreamErrorEnvelope)?.Error?.Message;
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Logs a failed provider response, so that an operator can find a bad API key or a provider that fails often.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="level">The log level.</param>
    /// <param name="providerName">The provider's name.</param>
    /// <param name="status">The HTTP status.</param>
    [LoggerMessage(Message = "The provider {ProviderName} returned status {Status}.")]
    private static partial void LogFailedResponse(ILogger logger, LogLevel level, string providerName, int status);
}

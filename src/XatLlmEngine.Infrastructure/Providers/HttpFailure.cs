using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>Translates failed provider responses into engine errors (anti-corruption layer).</summary>
internal static partial class HttpFailure
{
    /// <summary>Builds the error for a failed response of llama-server.</summary>
    /// <remarks>
    /// The engine forwards the server's own message only for problems that the caller can correct. Other messages stay hidden.
    /// </remarks>
    /// <param name="providerName">The provider's name, for the message.</param>
    /// <param name="response">The failed response.</param>
    /// <param name="body">The response body, or <see langword="null"/> when the engine could not read it.</param>
    /// <param name="logger">The log. It gets the provider's name and the status, never the body.</param>
    /// <returns>An error that is safe to show to the caller.</returns>
    public static EngineError FromResponse(string providerName, HttpResponseMessage response, string? body, ILogger logger)
    {
        var status = (int)response.StatusCode;
        var error = response.StatusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity =>
                new EngineError(ErrorKind.InvalidRequest, ReadMessage(body) ?? $"The provider '{providerName}' rejected the request."),
            HttpStatusCode.NotFound =>
                new EngineError(ErrorKind.NotFound, ReadMessage(body) ?? $"The provider '{providerName}' does not have this model."),
            HttpStatusCode.ServiceUnavailable =>
                new EngineError(ErrorKind.Unavailable, $"The provider '{providerName}' is temporarily unavailable."),

            // The engine makes a new key for each run of llama-server, so the user cannot correct a key. Another program can answer on the port.
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new EngineError(ErrorKind.Upstream, "llama-server refused the engine's key. Another program can use its port. Restart the engine."),
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

    /// <summary>Reads the error message of the server.</summary>
    /// <param name="body">The response body.</param>
    /// <returns>The message, or <see langword="null"/> when the body has no readable message.</returns>
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

    /// <summary>Logs a failed response of the server, so that an operator can find a server that fails often.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="level">The log level.</param>
    /// <param name="providerName">The provider's name.</param>
    /// <param name="status">The HTTP status.</param>
    [LoggerMessage(Message = "The provider {ProviderName} returned status {Status}.")]
    private static partial void LogFailedResponse(ILogger logger, LogLevel level, string providerName, int status);
}

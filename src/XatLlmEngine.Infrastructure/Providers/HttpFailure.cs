using System.Net;
using System.Text.Json;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>Translates failed provider responses into engine errors (anti-corruption layer).</summary>
internal static class HttpFailure
{
    /// <summary>Anthropic's non-standard status for "the API is temporarily overloaded".</summary>
    private const int Overloaded = 529;

    /// <summary>Builds the error for a failed response.</summary>
    /// <remarks>
    /// The provider's own message is passed on only for problems the caller can fix. Other messages stay hidden,
    /// because an authentication error from a provider can echo part of the engine's secret key.
    /// </remarks>
    /// <param name="providerName">The provider's name, for the message.</param>
    /// <param name="response">The failed response.</param>
    /// <param name="cancellationToken">Cancels reading the response body.</param>
    /// <returns>An error that is safe to show to the caller.</returns>
    public static async Task<EngineError> FromResponseAsync(
        string providerName,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity =>
                new EngineError(ErrorKind.InvalidRequest, await ReadMessageAsync(response, $"The provider '{providerName}' rejected the request.", cancellationToken).ConfigureAwait(false)),
            HttpStatusCode.NotFound =>
                new EngineError(ErrorKind.NotFound, await ReadMessageAsync(response, $"The provider '{providerName}' does not have this model.", cancellationToken).ConfigureAwait(false)),
            HttpStatusCode.TooManyRequests =>
                new EngineError(ErrorKind.RateLimited, $"The provider '{providerName}' is rate limiting requests."),
            HttpStatusCode.ServiceUnavailable or (HttpStatusCode)Overloaded =>
                new EngineError(ErrorKind.Unavailable, $"The provider '{providerName}' is temporarily unavailable."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new EngineError(ErrorKind.Upstream, $"The provider '{providerName}' rejected the engine's credentials. Check its configured API key."),
            _ => new EngineError(ErrorKind.Upstream, $"The provider '{providerName}' returned status {status}."),
        };
    }

    /// <summary>Builds the error for a provider that could not be reached or took too long.</summary>
    /// <param name="providerName">The provider's name, for the message.</param>
    /// <returns>An error that is safe to show to the caller.</returns>
    public static EngineError Unreachable(string providerName) =>
        new(ErrorKind.Unavailable, $"The provider '{providerName}' could not be reached in time.");

    /// <summary>Builds the error for a response the engine could not understand.</summary>
    /// <param name="providerName">The provider's name, for the message.</param>
    /// <returns>An error that is safe to show to the caller.</returns>
    public static EngineError Malformed(string providerName) =>
        new(ErrorKind.Upstream, $"The provider '{providerName}' returned a response the engine could not read.");

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
}

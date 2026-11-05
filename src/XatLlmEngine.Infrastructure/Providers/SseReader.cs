using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using XatLlmEngine.Application.Abstractions;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>Reads a provider's server-sent events, which both protocols use for streaming.</summary>
internal static class SseReader
{
    /// <summary>Yields each event and disposes the response when enumeration ends.</summary>
    /// <param name="providerName">The provider's name, for error messages.</param>
    /// <param name="response">A successful response, which this method takes ownership of.</param>
    /// <param name="logger">The log for a connection failure.</param>
    /// <param name="cancellationToken">Cancels reading, which also ends generation upstream.</param>
    /// <returns>The events in the order they arrive.</returns>
    /// <exception cref="ChatStreamException">The connection failed part-way through the stream.</exception>
    public static async IAsyncEnumerable<SseItem<string>> ReadAsync(
        string providerName,
        HttpResponseMessage response,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (response)
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var events = SseParser
                    .Create(stream, static (_, data) => Encoding.UTF8.GetString(data))
                    .EnumerateAsync(cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                await using (events.ConfigureAwait(false))
                {
                    while (await MoveNextAsync(providerName, events, logger).ConfigureAwait(false))
                    {
                        yield return events.Current;
                    }
                }
            }
        }
    }

    /// <summary>Advances the stream and translates connection failures. It is separate because C# forbids <c>yield</c> inside a <c>try</c> with a <c>catch</c>.</summary>
    /// <param name="providerName">The provider's name, for error messages.</param>
    /// <param name="events">The event enumerator.</param>
    /// <param name="logger">The log for a connection failure.</param>
    /// <returns><see langword="true"/> when another event is available.</returns>
    private static async ValueTask<bool> MoveNextAsync(string providerName, IAsyncEnumerator<SseItem<string>> events, ILogger logger)
    {
        try
        {
            return await events.MoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            throw new ChatStreamException(HttpFailure.Unreachable(providerName, exception, logger), exception);
        }
    }
}

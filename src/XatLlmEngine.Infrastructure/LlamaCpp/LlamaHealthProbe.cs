using System.Net.Http.Headers;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Asks a llama-server whether it is healthy, and whether it is the engine's own server.</summary>
/// <param name="httpClientFactory">Creates the HTTP client.</param>
internal sealed class LlamaHealthProbe(IHttpClientFactory httpClientFactory)
{
    /// <summary>The longest wait for one check, so that a server that does not respond cannot block the supervisor.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Checks once whether the server answers its health route. llama-server answers 503 while the model loads.</summary>
    /// <param name="baseUrl">The server's address, without the version path.</param>
    /// <param name="stoppingToken">Signals engine shutdown.</param>
    /// <returns><see langword="true"/> when the server reports that it is ready.</returns>
    public Task<bool> IsHealthyAsync(Uri baseUrl, CancellationToken stoppingToken) =>
        SucceedsAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, "health")), stoppingToken);

    /// <summary>Checks that the server knows the key of this run. Another program can take the port before llama-server binds it, but it cannot know the key.</summary>
    /// <param name="baseUrl">The server's address, without the version path.</param>
    /// <param name="apiKey">The key of this run.</param>
    /// <param name="stoppingToken">Signals engine shutdown.</param>
    /// <returns><see langword="true"/> when the server accepts the key on a route that needs it.</returns>
    public Task<bool> AcceptsKeyAsync(Uri baseUrl, string apiKey, CancellationToken stoppingToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, "props"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return SucceedsAsync(request, stoppingToken);
    }

    /// <summary>Sends one request with a time limit.</summary>
    /// <param name="request">The request. This method disposes it.</param>
    /// <param name="stoppingToken">Signals engine shutdown.</param>
    /// <returns><see langword="true"/> for a success status.</returns>
    private async Task<bool> SucceedsAsync(HttpRequestMessage request, CancellationToken stoppingToken)
    {
        using (request)
        {
            using var client = httpClientFactory.CreateClient(DependencyInjection.LocalHttpClientName);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            attempt.CancelAfter(RequestTimeout);
            try
            {
                using var response = await client.SendAsync(request, attempt.Token).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                // The port is not open.
                return false;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // This check timed out.
                return false;
            }
        }
    }
}

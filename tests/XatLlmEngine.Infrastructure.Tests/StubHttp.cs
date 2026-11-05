using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>An HTTP handler that returns a prepared response and records the request.</summary>
/// <param name="status">The status to return.</param>
/// <param name="body">The body to return.</param>
internal sealed class StubHttpHandler(HttpStatusCode status, string body) : HttpMessageHandler, IHttpClientFactory
{
    /// <summary>Gets the most recent request.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>Gets the most recent request body.</summary>
    public string? LastBody { get; private set; }

    /// <summary>Gets a failure to throw instead of responding, if any.</summary>
    public Exception? Throw { get; init; }

    /// <summary>Gets the Retry-After delay to send, if any.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <inheritdoc/>
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Throw is not null)
        {
            throw Throw;
        }

        LastRequest = request;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
        if (RetryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        return response;
    }
}

/// <summary>Shared request values. Each use gets a new body, because tests run in parallel.</summary>
internal static class TestRequests
{
    /// <summary>Gets a request with only a message.</summary>
    public static ChatRequest Minimal => Parse("""{"model":"test/model-x","messages":[{"role":"user","content":"Hello"}]}""");

    /// <summary>Builds a request from a JSON body.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The request, for the model <c>model-x</c>.</returns>
    public static ChatRequest Parse(string body) => new("model-x", JsonNode.Parse(body)!.AsObject());
}

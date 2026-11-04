using System.Net;
using System.Text;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

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
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
    }
}

/// <summary>Shared request values.</summary>
internal static class TestRequests
{
    /// <summary>A request with a system message and sampling values.</summary>
    public static readonly ChatRequest Full = new(
        "model-x",
        [new ChatMessage(ChatRole.System, "Be brief."), new ChatMessage(ChatRole.User, "Hello")],
        0.5,
        0.9,
        100,
        ["END"]);

    /// <summary>A request with only the required values.</summary>
    public static readonly ChatRequest Minimal = new("model-x", [new ChatMessage(ChatRole.User, "Hello")], null, null, null, []);
}

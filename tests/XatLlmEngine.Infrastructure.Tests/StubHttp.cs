using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Testing;
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

    /// <summary>Gets a failure to throw when the reader reaches the end of the body, if any. This simulates a connection that breaks part-way through a stream.</summary>
    public Exception? ThrowWhileReading { get; init; }

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
        HttpContent content = ThrowWhileReading is null
            ? new StringContent(body, Encoding.UTF8)
            : new StreamContent(new FailingStream(Encoding.UTF8.GetBytes(body), ThrowWhileReading));
        var response = new HttpResponseMessage(status) { Content = content };
        if (RetryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        return response;
    }
}

/// <summary>A stream that returns its data, and then throws a failure instead of ending.</summary>
/// <remarks>
/// The class derives from <see cref="Stream"/>, not from <see cref="MemoryStream"/>, so that every read path uses the two overrides below.
/// <see cref="Stream"/> sends ReadByte, Read(Span), CopyTo and the array form of ReadAsync to <see cref="Read(byte[], int, int)"/>, and sends CopyToAsync to <see cref="ReadAsync(Memory{byte}, CancellationToken)"/>.
/// </remarks>
/// <param name="data">The data to return first.</param>
/// <param name="failure">The failure to throw when the reader reaches the end of the data.</param>
internal sealed class FailingStream(byte[] data, Exception failure) : Stream
{
    /// <summary>The data to return.</summary>
    private readonly MemoryStream inner = new(data, writable: false);

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        return read > 0 ? read : throw failure;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        return read > 0 ? read : throw failure;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Checks on recorded log entries.</summary>
internal static class TestLogs
{
    /// <summary>Asserts that no log entry holds any of the given texts, in its message, its exception or its structured values.</summary>
    /// <param name="logger">The logger that recorded the entries.</param>
    /// <param name="secrets">The texts that must not appear.</param>
    public static void AssertHoldsNone(FakeLogger logger, params string[] secrets)
    {
        foreach (var record in logger.Collector.GetSnapshot())
        {
            var logged = string.Join(' ', [record.Message, record.Exception?.ToString(), .. (record.StructuredState ?? []).Select(pair => pair.Value)]);
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, logged, StringComparison.Ordinal);
            }
        }
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

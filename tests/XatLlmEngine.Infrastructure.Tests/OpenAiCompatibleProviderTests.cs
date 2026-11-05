using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Polly.Timeout;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.Providers;
using XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the OpenAI protocol adapter against responses in the vendor's documented format.</summary>
public class OpenAiCompatibleProviderTests
{
    /// <summary>A documented non-streaming response.</summary>
    private const string CompletionBody =
        """{"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Hi!"},"finish_reason":"length"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}""";

    /// <summary>A documented stream, including a role-only piece and a final usage piece.</summary>
    private const string StreamBody =
        """
        data: {"choices":[{"delta":{"role":"assistant","content":""},"finish_reason":null}]}

        data: {"choices":[{"delta":{"content":"Hi"},"finish_reason":null}]}

        data: {"choices":[{"delta":{"content":" there"},"finish_reason":null}]}

        data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

        data: {"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}

        data: [DONE]


        """;

    /// <summary>The request has the expected address and key, and the caller's fields arrive unchanged, including ones the engine does not know.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_SendsExpectedRequest()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, CompletionBody);
        var request = TestRequests.Parse("""{"model":"test/model-x","stream":false,"top_p":0.9,"max_completion_tokens":100,"stop":"END","vendor_field":{"a":1},"messages":[{"role":"developer","content":"Be brief."},{"role":"user","content":[{"type":"image_url","image_url":{"url":"x"}}]}]}""");

        await Provider(http).CompleteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("https://api.example.com/v1/chat/completions", http.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer secret", http.LastRequest.Headers.Authorization!.ToString());
        using var body = JsonDocument.Parse(http.LastBody!);
        Assert.Equal("model-x", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("developer", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("x", body.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal(100, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(0.9, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal("END", body.RootElement.GetProperty("stop").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("vendor_field").GetProperty("a").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("stream", out _));
    }

    /// <summary>Values the caller left out are not sent, so that the provider's defaults apply.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_OptionalValuesMissing_LeavesThemOut()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, CompletionBody);

        await Provider(http).CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(http.LastBody!);
        foreach (var name in new[] { "temperature", "top_p", "max_tokens", "max_completion_tokens", "stop" })
        {
            Assert.False(body.RootElement.TryGetProperty(name, out _), name);
        }
    }

    /// <summary>Sending a request leaves the caller's body unchanged.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_DoesNotChangeCallersBody()
    {
        var request = TestRequests.Minimal;

        await Provider(new StubHttpHandler(HttpStatusCode.OK, CompletionBody)).CompleteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("test/model-x", request.Body["model"]!.GetValue<string>());
    }

    /// <summary>The provider converts the answer, finish reason and token counts.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ValidResponse_ReturnsAnswer()
    {
        var result = await Provider(new StubHttpHandler(HttpStatusCode.OK, CompletionBody))
            .CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(new ChatCompletion("Hi!", FinishReason.Length, new TokenUsage(5, 2)), result.Value);
    }

    /// <summary>Provider statuses map to the engine's error categories.</summary>
    /// <param name="status">The provider's status.</param>
    /// <param name="kind">The expected category.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.NotFound, ErrorKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErrorKind.Unavailable)]
    [InlineData((HttpStatusCode)529, ErrorKind.Unavailable)]
    [InlineData(HttpStatusCode.Unauthorized, ErrorKind.Upstream)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorKind.Upstream)]
    public async Task Complete_ErrorStatus_MapsToKind(HttpStatusCode status, ErrorKind kind)
    {
        var result = await Provider(new StubHttpHandler(status, """{"error":{"message":"details"}}"""))
            .CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(kind, result.Error!.Kind);
    }

    /// <summary>A temporary failure keeps the provider's requested wait, so that the engine can pass it to the caller.</summary>
    /// <param name="status">The provider's status.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData((HttpStatusCode)529)]
    public async Task Complete_TemporaryFailureWithRetryAfter_KeepsDelay(HttpStatusCode status)
    {
        var http = new StubHttpHandler(status, "{}") { RetryAfter = TimeSpan.FromSeconds(12) };

        var result = await Provider(http).CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(12), result.Error!.RetryAfter);
    }

    /// <summary>A temporary failure without a Retry-After header has no requested wait.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_TemporaryFailureWithoutRetryAfter_HasNoDelay()
    {
        var result = await Provider(new StubHttpHandler(HttpStatusCode.TooManyRequests, "{}"))
            .CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Null(result.Error!.RetryAfter);
    }

    /// <summary>A fixable problem shows the provider's explanation.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_BadRequest_ShowsProviderMessage()
    {
        var result = await Provider(new StubHttpHandler(HttpStatusCode.BadRequest, """{"error":{"message":"temperature is too high"}}"""))
            .CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal("temperature is too high", result.Error!.Message);
    }

    /// <summary>An authentication error can echo part of the engine's key, so its text must never reach callers.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_Unauthorized_HidesProviderMessage()
    {
        var result = await Provider(new StubHttpHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided: sk-abc"}}"""))
            .CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("sk-abc", result.Error!.Message, StringComparison.Ordinal);
    }

    /// <summary>A body that is not JSON still gives a clean error.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_NonJsonSuccess_FailsAsUpstream()
    {
        var result = await Provider(new StubHttpHandler(HttpStatusCode.OK, "<html>proxy error</html>"))
            .CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Upstream, result.Error!.Kind);
    }

    /// <summary>The provider reports a connection failure as temporarily unavailable.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ConnectionFails_FailsAsUnavailable()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, string.Empty) { Throw = new HttpRequestException("refused") };

        var result = await Provider(http).CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error!.Kind);
    }

    /// <summary>A failed response causes one log entry with the provider and the status. The entry never holds the response body or the API key.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ErrorStatus_LogsWithoutBodyOrKey()
    {
        var logger = new FakeLogger();
        var http = new StubHttpHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided: sk-abc"}}""");

        await Provider(http, logger).CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("401", record.Message, StringComparison.Ordinal);
        TestLogs.AssertHoldsNone(logger, "sk-abc", "secret");
    }

    /// <summary>An error that the caller can correct is logged at Debug level only, because the caller already gets the message.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_BadRequest_LogsAtDebug()
    {
        var logger = new FakeLogger();

        await Provider(new StubHttpHandler(HttpStatusCode.BadRequest, "{}"), logger).CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Collector.GetSnapshot()).Level);
    }

    /// <summary>A provider that the engine cannot reach is logged with the cause.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ConnectionFails_LogsWarning()
    {
        var logger = new FakeLogger();
        var failure = new HttpRequestException("refused");
        var http = new StubHttpHandler(HttpStatusCode.OK, string.Empty) { Throw = failure };

        await Provider(http, logger).CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Same(failure, record.Exception);
    }

    /// <summary>A provider that takes too long causes a temporary failure. The engine logs the failure with its cause.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_Timeout_LogsWarningAndFailsAsUnavailable()
    {
        var logger = new FakeLogger();
        var failure = new TimeoutRejectedException();
        var http = new StubHttpHandler(HttpStatusCode.OK, string.Empty) { Throw = failure };

        var result = await Provider(http, logger).CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error!.Kind);
        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Same(failure, record.Exception);
    }

    /// <summary>A failed status before a stream starts causes one log entry with the provider and the status. The entry never holds the response body or the API key.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ErrorStatus_LogsWithoutBodyOrKey()
    {
        var logger = new FakeLogger();
        var http = new StubHttpHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided: sk-abc"}}""");

        await Provider(http, logger).StreamAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("401", record.Message, StringComparison.Ordinal);
        TestLogs.AssertHoldsNone(logger, "sk-abc", "secret");
    }

    /// <summary>A provider that the engine cannot reach before a stream starts is logged with the cause.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ConnectionFails_LogsWarning()
    {
        var logger = new FakeLogger();
        var failure = new HttpRequestException("refused");
        var http = new StubHttpHandler(HttpStatusCode.OK, string.Empty) { Throw = failure };

        var result = await Provider(http, logger).StreamAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error!.Kind);
        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Same(failure, record.Exception);
    }

    /// <summary>A connection that breaks part-way through a stream causes a temporary failure. The engine logs the failure with its cause.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_FailsPartWay_LogsWarningAndThrows()
    {
        var logger = new FakeLogger();
        var failure = new IOException("reset");
        var http = new StubHttpHandler(HttpStatusCode.OK, "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n") { ThrowWhileReading = failure };

        var result = await Provider(http, logger).StreamAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);
        await using var chunks = result.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        // The first chunk arrives before the connection breaks, so the failure occurs part-way through the stream.
        Assert.True(await chunks.MoveNextAsync());
        Assert.Equal("Hi", chunks.Current.Content);
        var exception = await Assert.ThrowsAsync<ChatStreamException>(async () => await chunks.MoveNextAsync());
        Assert.Equal(ErrorKind.Unavailable, exception.Error.Kind);
        Assert.Same(failure, exception.InnerException);
        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Same(failure, record.Exception);
    }

    /// <summary>The provider converts stream pieces in order, and skips empty pieces.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ValidEvents_ReturnsChunks()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, StreamBody);
        var request = TestRequests.Parse("""{"model":"test/model-x","stream_options":{"include_usage":true},"messages":[]}""");

        var result = await Provider(http).StreamAsync(request, TestContext.Current.CancellationToken);
        var chunks = await result.Value!.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new ChatChunk("Hi", null, null),
                new ChatChunk(" there", null, null),
                new ChatChunk(null, FinishReason.Stop, null),
                new ChatChunk(null, null, new TokenUsage(5, 2)),
            ],
            chunks);
        using var body = JsonDocument.Parse(http.LastBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    /// <summary>The provider removes token counts when the caller did not ask for them, even if the server sends them.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_UsageNotRequested_DropsUsage()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, StreamBody);

        var result = await Provider(http).StreamAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);
        var chunks = await result.Value!.ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(chunks, chunk => Assert.Null(chunk.Usage));
        using var body = JsonDocument.Parse(http.LastBody!);
        Assert.False(body.RootElement.TryGetProperty("stream_options", out _));
    }

    /// <summary>A failure before the stream starts is a normal result.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ErrorStatus_ReturnsFailure()
    {
        var result = await Provider(new StubHttpHandler(HttpStatusCode.TooManyRequests, "{}"))
            .StreamAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.RateLimited, result.Error!.Kind);
    }

    /// <summary>The provider throws an error event that comes after the stream starts, because it can no longer return a result.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ErrorEvent_Throws()
    {
        var result = await Provider(new StubHttpHandler(HttpStatusCode.OK, "data: {\"error\":{\"message\":\"overloaded\"}}\n\n"))
            .StreamAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ChatStreamException>(async () =>
            await result.Value!.ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Creates the adapter.</summary>
    /// <param name="http">The stubbed network.</param>
    /// <param name="logger">The log, or <see langword="null"/> for no log.</param>
    /// <returns>The adapter.</returns>
    private static OpenAiCompatibleChatProvider Provider(StubHttpHandler http, ILogger? logger = null) => new(
        "test",
        new ProviderOptions { BaseUrl = new Uri("https://api.example.com/v1/"), ApiKey = "secret" },
        new OpenAiClient("test", http, "test", TimeProvider.System, logger ?? NullLogger.Instance));
}

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.Providers;
using XatLlmEngine.Infrastructure.Providers.Anthropic;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the Anthropic adapter against responses in the vendor's documented format.</summary>
public class AnthropicProviderTests
{
    /// <summary>A documented non-streaming response.</summary>
    private const string CompletionBody =
        """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"Hello"},{"type":"text","text":" world"}],"model":"claude-x","stop_reason":"max_tokens","usage":{"input_tokens":12,"output_tokens":6}}""";

    /// <summary>A documented stream, including events the engine ignores.</summary>
    private const string StreamBody =
        """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","content":[],"stop_reason":null,"usage":{"input_tokens":25,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: ping
        data: {"type":"ping"}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":15}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    /// <summary>The request has Anthropic's headers, and the caller's fields arrive unchanged in Anthropic's own format.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_SendsExpectedRequest()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, CompletionBody);
        var request = TestRequests.Parse("""{"model":"anthropic/model-x","system":"Be brief.","max_tokens":100,"stop_sequences":["END"],"top_k":5,"messages":[{"role":"user","content":[{"type":"text","text":"Hello"}]}]}""");

        await Provider(http).CompleteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("https://api.anthropic.test/v1/messages", http.LastRequest!.RequestUri!.ToString());
        Assert.Equal("secret", http.LastRequest.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", http.LastRequest.Headers.GetValues("anthropic-version").Single());
        using var body = JsonDocument.Parse(http.LastBody!);
        Assert.Equal("model-x", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Be brief.", body.RootElement.GetProperty("system").GetString());
        Assert.Equal(100, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("END", body.RootElement.GetProperty("stop_sequences")[0].GetString());
        Assert.Equal(5, body.RootElement.GetProperty("top_k").GetInt32());
        Assert.Equal("Hello", body.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>The adapter adds no fields of its own. The Application layer adds the token limit, which Anthropic requires.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_Body_HasOnlyCallerFields()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, CompletionBody);

        await Provider(http).CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(http.LastBody!);
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("system", out _));
        Assert.Equal(777, Provider(http).MaxTokensLimit);
    }

    /// <summary>The provider joins text blocks, and converts the stop reason and token counts.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ValidResponse_ReturnsAnswer()
    {
        var result = await Provider(new StubHttpHandler(HttpStatusCode.OK, CompletionBody))
            .CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(new ChatCompletion("Hello world", FinishReason.Length, new TokenUsage(12, 6)), result.Value);
    }

    /// <summary>Anthropic's overloaded status is a temporary failure.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_Overloaded_FailsAsUnavailable()
    {
        var result = await Provider(new StubHttpHandler((HttpStatusCode)529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""))
            .CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error!.Kind);
    }

    /// <summary>A rate limit keeps the provider's requested wait, so that the engine can pass it to the caller.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_RateLimitedWithRetryAfter_KeepsDelay()
    {
        var http = new StubHttpHandler(HttpStatusCode.TooManyRequests, """{"type":"error","error":{"type":"rate_limit_error","message":"Slow down"}}""")
        {
            RetryAfter = TimeSpan.FromSeconds(12),
        };

        var result = await Provider(http).CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.RateLimited, result.Error!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(12), result.Error.RetryAfter);
    }

    /// <summary>A failed response causes one log entry with the provider and the status. The entry never holds the response body or the API key.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ErrorStatus_LogsWithoutBodyOrKey()
    {
        var logger = new FakeLogger<AnthropicChatProvider>();
        var http = new StubHttpHandler(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key sk-abc"}}""");

        await Provider(http, logger).CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("401", record.Message, StringComparison.Ordinal);
        TestLogs.AssertHoldsNone(logger, "sk-abc", "secret");
    }

    /// <summary>A provider that the engine cannot reach causes a temporary failure. The engine logs the failure with its cause.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ConnectionFails_LogsWarning()
    {
        var logger = new FakeLogger<AnthropicChatProvider>();
        var failure = new HttpRequestException("refused");
        var http = new StubHttpHandler(HttpStatusCode.OK, string.Empty) { Throw = failure };

        var result = await Provider(http, logger).CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error!.Kind);
        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Same(failure, record.Exception);
    }

    /// <summary>The provider reports a refusal as a content filter stop.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_Refusal_ReportsContentFilter()
    {
        var result = await Provider(new StubHttpHandler(HttpStatusCode.OK, """{"content":[],"stop_reason":"refusal"}"""))
            .CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(FinishReason.ContentFilter, result.Value!.FinishReason);
    }

    /// <summary>Text deltas become chunks, and the input count from the first event joins the output count from the last.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ValidEvents_ReturnsChunks()
    {
        var result = await Provider(new StubHttpHandler(HttpStatusCode.OK, StreamBody))
            .StreamAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);
        var chunks = await result.Value!.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new ChatChunk("Hello", null, null),
                new ChatChunk(" world", null, null),
                new ChatChunk(null, FinishReason.Stop, new TokenUsage(25, 15)),
            ],
            chunks);
    }

    /// <summary>The provider throws an error event that comes after the stream starts.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ErrorEvent_Throws()
    {
        var result = await Provider(new StubHttpHandler(HttpStatusCode.OK, "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n"))
            .StreamAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ChatStreamException>(async () =>
            await result.Value!.ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A connection that breaks part-way through a stream causes a temporary failure. The engine logs the failure with its cause.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_FailsPartWay_LogsWarningAndThrows()
    {
        var logger = new FakeLogger<AnthropicChatProvider>();
        var failure = new IOException("reset");
        const string FirstEvents = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","content":[],"stop_reason":null,"usage":{"input_tokens":25,"output_tokens":1}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}


            """;
        var http = new StubHttpHandler(HttpStatusCode.OK, FirstEvents) { ThrowWhileReading = failure };

        var result = await Provider(http, logger).StreamAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);
        await using var chunks = result.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        // The first chunk arrives before the connection breaks, so the failure occurs part-way through the stream.
        Assert.True(await chunks.MoveNextAsync());
        Assert.Equal("Hello", chunks.Current.Content);
        var exception = await Assert.ThrowsAsync<ChatStreamException>(async () => await chunks.MoveNextAsync());
        Assert.Equal(ErrorKind.Unavailable, exception.Error.Kind);
        Assert.Same(failure, exception.InnerException);
        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Same(failure, record.Exception);
    }

    /// <summary>Creates the adapter.</summary>
    /// <param name="http">The stubbed network.</param>
    /// <param name="logger">The log, or <see langword="null"/> for no log.</param>
    /// <returns>The adapter.</returns>
    private static AnthropicChatProvider Provider(StubHttpHandler http, ILogger<AnthropicChatProvider>? logger = null) => new(
        "anthropic",
        new ProviderOptions { Type = ProviderType.Anthropic, BaseUrl = new Uri("https://api.anthropic.test"), ApiKey = "secret", MaxTokensLimit = 777 },
        TestTransports.Create(http, logger, "anthropic"));
}

using System.Net;
using System.Text.Json;
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

    /// <summary>The request has the expected address, key and values.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_SendsExpectedRequest()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, CompletionBody);

        await Provider(http).CompleteAsync(TestRequests.Full, TestContext.Current.CancellationToken);

        Assert.Equal("https://api.example.com/v1/chat/completions", http.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer secret", http.LastRequest.Headers.Authorization!.ToString());
        using var body = JsonDocument.Parse(http.LastBody!);
        Assert.Equal("model-x", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal(100, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.9, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal("END", body.RootElement.GetProperty("stop")[0].GetString());
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

    /// <summary>OpenAI's own API needs the newer token limit field.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_UsesMaxCompletionTokens_SendsNewerField()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, CompletionBody);

        await Provider(http, useMaxCompletionTokens: true).CompleteAsync(TestRequests.Full, TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(http.LastBody!);
        Assert.Equal(100, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
    }

    /// <summary>The answer, finish reason and token counts are converted.</summary>
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

    /// <summary>A connection failure is reported as temporarily unavailable.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ConnectionFails_FailsAsUnavailable()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, string.Empty) { Throw = new HttpRequestException("refused") };

        var result = await Provider(http).CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error!.Kind);
    }

    /// <summary>Stream pieces are converted, in order, and empty pieces are skipped.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ValidEvents_ReturnsChunks()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, StreamBody);

        var result = await Provider(http).StreamAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);
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

    /// <summary>A failure before the stream starts is a normal result.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ErrorStatus_ReturnsFailure()
    {
        var result = await Provider(new StubHttpHandler(HttpStatusCode.TooManyRequests, "{}"))
            .StreamAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.RateLimited, result.Error!.Kind);
    }

    /// <summary>An error event after the stream starts is thrown, because a result can no longer be returned.</summary>
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
    /// <param name="useMaxCompletionTokens">Whether to send the newer token limit field.</param>
    /// <returns>The adapter.</returns>
    private static OpenAiCompatibleChatProvider Provider(StubHttpHandler http, bool useMaxCompletionTokens = false) => new(
        "test",
        new ProviderOptions { BaseUrl = new Uri("https://api.example.com/v1/"), ApiKey = "secret" },
        new OpenAiClient("test", http, "test", useMaxCompletionTokens));
}

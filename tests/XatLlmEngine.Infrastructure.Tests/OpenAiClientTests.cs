using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.Providers;
using XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the OpenAI protocol client against responses in the documented format.</summary>
public class OpenAiClientTests
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

    /// <summary>Gets the base address of the stubbed server.</summary>
    private static Uri Address { get; } = new("http://127.0.0.1:8080/v1/");

    /// <summary>A complete answer has the text, the finish reason and the token counts.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ValidBody_ReadsAnswer()
    {
        var result = await Client(new StubHttpHandler(HttpStatusCode.OK, CompletionBody)).CompleteAsync(Address, "secret", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal("Hi!", result.Value?.Content);
        Assert.Equal(FinishReason.Length, result.Value?.FinishReason);
        Assert.Equal(new TokenUsage(5, 2), result.Value?.Usage);
    }

    /// <summary>An answer with no text is an error, not an empty answer.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_NoContent_FailsAsUpstream()
    {
        var result = await Client(new StubHttpHandler(HttpStatusCode.OK, """{"choices":[]}""")).CompleteAsync(Address, "secret", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Upstream, result.Error?.Kind);
    }

    /// <summary>A stream gives the text pieces in order, and skips the piece with a role only.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ValidBody_ReadsChunks()
    {
        var result = await Client(new StubHttpHandler(HttpStatusCode.OK, StreamBody)).StreamAsync(Address, "secret", TestRequests.Minimal, TestContext.Current.CancellationToken);
        var chunks = await result.Value!.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Hi there", string.Concat(chunks.Select(chunk => chunk.Content)));
        Assert.Contains(chunks, chunk => chunk.FinishReason == FinishReason.Stop);
    }

    /// <summary>The request has the expected address and key, and the caller's fields arrive unchanged, including ones the engine does not know.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_SendsExpectedRequest()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, CompletionBody);
        var request = TestRequests.Parse("""{"model":"test/model-x","stream":false,"top_p":0.9,"max_completion_tokens":100,"stop":"END","vendor_field":{"a":1},"messages":[{"role":"developer","content":"Be brief."},{"role":"user","content":[{"type":"image_url","image_url":{"url":"x"}}]}]}""");

        await Client(http).CompleteAsync(Address, "secret", request, TestContext.Current.CancellationToken);

        Assert.Equal("http://127.0.0.1:8080/v1/chat/completions", http.LastRequest!.RequestUri!.ToString());
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

        await Client(http).CompleteAsync(Address, "secret", TestRequests.Minimal, TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(http.LastBody!);
        foreach (var name in new[] { "temperature", "top_p", "max_tokens", "max_completion_tokens", "stop" })
        {
            Assert.False(body.RootElement.TryGetProperty(name, out _), name);
        }
    }

    /// <summary>When the server accepts stream_options, the client always asks for token counts, for the metrics and the logs. The caller's other stream options stay.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ServerAcceptsStreamOptions_AsksForUsage()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, StreamBody);
        var client = new OpenAiClient(TestTransports.Create(http), requestStreamUsage: true);
        var request = TestRequests.Parse("""{"stream_options":{"include_obfuscation":false},"messages":[]}""");

        var result = await client.StreamAsync(Address, "secret", request, TestContext.Current.CancellationToken);
        var chunks = await result.Value!.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(chunks, chunk => chunk.Usage is not null);
        using var body = JsonDocument.Parse(http.LastBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.False(body.RootElement.GetProperty("stream_options").GetProperty("include_obfuscation").GetBoolean());
    }

    /// <summary>When the server does not accept stream_options, the client does not add it.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ServerWithoutStreamOptions_AddsNothing()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, StreamBody);

        var result = await Client(http).StreamAsync(Address, "secret", TestRequests.Minimal, TestContext.Current.CancellationToken);
        await result.Value!.ToListAsync(TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(http.LastBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("stream_options", out _));
    }

    /// <summary>The caller gets the token counts of a stream only when it asked for them. A server that does not accept stream_options always forwards counts when present.</summary>
    /// <param name="requestStreamUsage">Whether the server accepts stream_options.</param>
    /// <param name="streamOptionsJson">The JSON value of stream_options, or <see langword="null"/> when omitted.</param>
    /// <param name="expected">Whether the caller must get the counts.</param>
    [Theory]
    [InlineData(true, null, false)]
    [InlineData(true, """{"include_usage":false}""", false)]
    [InlineData(true, """{"include_usage":"true"}""", false)]
    [InlineData(true, """{"include_usage":true}""", true)]
    [InlineData(false, null, true)]
    public void ShouldForwardStreamUsage_FollowsCallerAndServer(bool requestStreamUsage, string? streamOptionsJson, bool expected)
    {
        var client = new OpenAiClient(TestTransports.Create(new StubHttpHandler(HttpStatusCode.OK, CompletionBody)), requestStreamUsage);
        var body = new JsonObject { ["messages"] = new JsonArray() };
        if (streamOptionsJson is not null)
        {
            body["stream_options"] = JsonNode.Parse(streamOptionsJson);
        }

        Assert.Equal(expected, client.ShouldForwardStreamUsage(body));
    }

    /// <summary>The client throws an error event that comes after the stream starts, because it can no longer return a result.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ErrorEvent_Throws()
    {
        var result = await Client(new StubHttpHandler(HttpStatusCode.OK, "data: {\"error\":{\"message\":\"overloaded\"}}\n\n"))
            .StreamAsync(Address, "secret", TestRequests.Minimal, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ChatStreamException>(async () =>
            await result.Value!.ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Builds a client over the stubbed network.</summary>
    /// <param name="http">The stubbed network.</param>
    /// <returns>The client.</returns>
    private static OpenAiClient Client(StubHttpHandler http) => new(TestTransports.Create(http));
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Host.Tests;

/// <summary>Tests the HTTP contract end to end through the real pipeline.</summary>
public class ApiTests
{
    /// <summary>A valid request body.</summary>
    private const string ValidBody = """{"model":"fake/model-a","messages":[{"role":"user","content":"Hi"}]}""";

    /// <summary>A complete answer follows the OpenAI format.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Chat_ValidRequest_ReturnsOpenAiFormat()
    {
        await using var engine = new EngineFactory();

        using var response = await PostAsync(engine, ValidBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.StartsWith("chatcmpl-", root.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal("fake/model-a", root.GetProperty("model").GetString());
        var choice = root.GetProperty("choices")[0];
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Hello!", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal(5, root.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    /// <summary>All the accepted fields reach the provider, including both forms of message content and stop.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Chat_AllFields_ReachProvider()
    {
        await using var engine = new EngineFactory();
        const string Body = """
            {"model":"fake/model-a","temperature":0.5,"top_p":0.9,"max_tokens":10,"max_completion_tokens":20,"stop":"END",
             "messages":[{"role":"developer","content":"Be brief."},
                         {"role":"user","content":[{"type":"text","text":"Hello "},{"type":"text","text":"there"}]}]}
            """;

        using var response = await PostAsync(engine, Body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = engine.Provider.LastRequest!;
        Assert.Equal("model-a", request.Model);
        Assert.Equal(0.5, request.Temperature);
        Assert.Equal(0.9, request.TopP);
        Assert.Equal(20, request.MaxTokens);
        Assert.Equal(["END"], request.Stop);
        Assert.Equal(new ChatMessage(ChatRole.System, "Be brief."), request.Messages[0]);
        Assert.Equal(new ChatMessage(ChatRole.User, "Hello there"), request.Messages[1]);
    }

    /// <summary>Bad requests return 400 in the OpenAI error format, and unsupported features are never silently ignored.</summary>
    /// <param name="body">The request body.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"model":"fake/model-a","temperature":"hot","messages":[{"role":"user","content":"Hi"}]}""")]
    [InlineData("""{"model":"no-slash","messages":[{"role":"user","content":"Hi"}]}""")]
    [InlineData("""{"model":"fake/model-a","messages":[]}""")]
    [InlineData("""{"model":"fake/model-a"}""")]
    [InlineData("""{"model":"fake/model-a","messages":[null]}""")]
    [InlineData("""{"model":"fake/model-a","messages":[{"role":"tool","content":"Hi"}]}""")]
    [InlineData("""{"model":"fake/model-a","messages":[{"role":"user","content":null}]}""")]
    [InlineData("""{"model":"fake/model-a","messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"x"}}]}]}""")]
    [InlineData("""{"model":"fake/model-a","stop":5,"messages":[{"role":"user","content":"Hi"}]}""")]
    [InlineData("""{"model":"fake/model-a","n":2,"messages":[{"role":"user","content":"Hi"}]}""")]
    [InlineData("""{"model":"fake/model-a","tools":[{"type":"function"}],"messages":[{"role":"user","content":"Hi"}]}""")]
    [InlineData("""{"model":"fake/model-a","response_format":{"type":"json_object"},"messages":[{"role":"user","content":"Hi"}]}""")]
    public async Task Chat_BadRequest_ReturnsErrorFormat(string body)
    {
        await using var engine = new EngineFactory();

        using var response = await PostAsync(engine, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("error").GetProperty("message").GetString()));
        Assert.Equal("invalid_request_error", json.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    /// <summary>Other content types are refused, because that blocks cross-site requests from browsers.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Chat_NotJsonContentType_Returns415()
    {
        await using var engine = new EngineFactory();
        using var client = engine.CreateClient();
        using var content = new StringContent(ValidBody, Encoding.UTF8, "text/plain");

        using var response = await client.PostAsync(new Uri("/v1/chat/completions", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    /// <summary>An unknown provider returns 404.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Chat_UnknownProvider_Returns404()
    {
        await using var engine = new EngineFactory();

        using var response = await PostAsync(engine, """{"model":"nobody/model","messages":[{"role":"user","content":"Hi"}]}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Provider failures map to HTTP statuses, and temporary ones tell callers when to retry.</summary>
    /// <param name="kind">The failure category.</param>
    /// <param name="status">The expected status.</param>
    /// <param name="retryAfter">Whether a Retry-After header is expected.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData(ErrorKind.RateLimited, HttpStatusCode.TooManyRequests, true)]
    [InlineData(ErrorKind.Unavailable, HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(ErrorKind.Upstream, HttpStatusCode.BadGateway, false)]
    public async Task Chat_ProviderFails_MapsStatus(ErrorKind kind, HttpStatusCode status, bool retryAfter)
    {
        await using var engine = new EngineFactory();
        engine.Provider.Failure = new EngineError(kind, "Problem.");

        using var response = await PostAsync(engine, ValidBody);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(retryAfter, response.Headers.RetryAfter is not null);
    }

    /// <summary>An unexpected failure returns 500 in the OpenAI format, and never shows its internal details.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Chat_UnexpectedFailure_Returns500WithoutDetails()
    {
        await using var engine = new EngineFactory();
        engine.Provider.Crash = new InvalidOperationException("internal secret detail");
        var production = engine.WithWebHostBuilder(builder => Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseEnvironment(builder, "Production"));
        using var client = production.CreateClient();
        using var content = new StringContent(ValidBody, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(new Uri("/v1/chat/completions", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("internal secret detail", text, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("internal_error", body.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    /// <summary>A local request without an installed model returns 503, and the engine itself keeps working.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Chat_LocalModelMissing_Returns503()
    {
        await using var engine = new EngineFactory();

        using var response = await PostAsync(engine, """{"model":"local/ministral-3-3b","messages":[{"role":"user","content":"Hi"}]}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    /// <summary>A stream follows the OpenAI format: a role piece, text, a finish piece, a usage piece, then the end marker.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ValidRequest_WritesOpenAiEvents()
    {
        await using var engine = new EngineFactory();
        engine.Provider.Chunks = [new ChatChunk("Hel", null, null), new ChatChunk("lo", null, null), new ChatChunk(null, FinishReason.Length, new TokenUsage(3, 2))];

        using var response = await PostAsync(engine, """{"model":"fake/model-a","stream":true,"stream_options":{"include_usage":true},"messages":[{"role":"user","content":"Hi"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var events = await ReadEventsAsync(response);
        Assert.Equal("[DONE]", events[^1]);

        using var role = JsonDocument.Parse(events[0]);
        Assert.Equal("chat.completion.chunk", role.RootElement.GetProperty("object").GetString());
        var firstChoice = role.RootElement.GetProperty("choices")[0];
        Assert.Equal("assistant", firstChoice.GetProperty("delta").GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, firstChoice.GetProperty("finish_reason").ValueKind);

        using var text = JsonDocument.Parse(events[1]);
        Assert.Equal("Hel", text.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());

        using var finish = JsonDocument.Parse(events[3]);
        Assert.Equal("length", finish.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());

        using var usage = JsonDocument.Parse(events[4]);
        Assert.Equal(0, usage.RootElement.GetProperty("choices").GetArrayLength());
        Assert.Equal(5, usage.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    /// <summary>Usage is sent only on request, because some clients cannot handle a piece without choices.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_UsageNotRequested_LeavesOutUsagePiece()
    {
        await using var engine = new EngineFactory();
        engine.Provider.Chunks = [new ChatChunk("Hi", FinishReason.Stop, new TokenUsage(3, 2))];

        using var response = await PostAsync(engine, """{"model":"fake/model-a","stream":true,"messages":[{"role":"user","content":"Hi"}]}""");

        var events = await ReadEventsAsync(response);
        Assert.Equal(3, events.Count);
        Assert.DoesNotContain(events, item => item.Contains("usage", StringComparison.Ordinal));
    }

    /// <summary>A failure before any text is a normal HTTP error.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_FailsBeforeStart_ReturnsHttpError()
    {
        await using var engine = new EngineFactory();
        engine.Provider.Failure = new EngineError(ErrorKind.RateLimited, "Slow down.");

        using var response = await PostAsync(engine, """{"model":"fake/model-a","stream":true,"messages":[{"role":"user","content":"Hi"}]}""");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    /// <summary>A failure in the very first event is still a normal HTTP error, because nothing has been written yet.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_FailsOnFirstEvent_ReturnsHttpError()
    {
        await using var engine = new EngineFactory();
        engine.Provider.ThrowAfterChunks = new ChatStreamException(new EngineError(ErrorKind.RateLimited, "Slow down."));

        using var response = await PostAsync(engine, """{"model":"fake/model-a","stream":true,"messages":[{"role":"user","content":"Hi"}]}""");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    /// <summary>A stream with no events at all is a provider failure.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_NoEvents_ReturnsHttpError()
    {
        await using var engine = new EngineFactory();

        using var response = await PostAsync(engine, """{"model":"fake/model-a","stream":true,"messages":[{"role":"user","content":"Hi"}]}""");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    /// <summary>A failure after text becomes an error event, and the missing end marker shows that the answer is incomplete.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_FailsPartWay_WritesErrorEventWithoutEndMarker()
    {
        await using var engine = new EngineFactory();
        engine.Provider.Chunks = [new ChatChunk("Hel", null, null)];
        engine.Provider.ThrowAfterChunks = new ChatStreamException(new EngineError(ErrorKind.Upstream, "Connection lost."));

        using var response = await PostAsync(engine, """{"model":"fake/model-a","stream":true,"messages":[{"role":"user","content":"Hi"}]}""");

        var events = await ReadEventsAsync(response);
        Assert.DoesNotContain("[DONE]", events);
        using var error = JsonDocument.Parse(events[^1]);
        Assert.Equal("Connection lost.", error.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    /// <summary>A stream that ends with no finish reason was cut short, so it must not look complete.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_EndsWithoutFinishReason_WritesErrorEvent()
    {
        await using var engine = new EngineFactory();
        engine.Provider.Chunks = [new ChatChunk("Hel", null, null)];

        using var response = await PostAsync(engine, """{"model":"fake/model-a","stream":true,"messages":[{"role":"user","content":"Hi"}]}""");

        var events = await ReadEventsAsync(response);
        Assert.DoesNotContain("[DONE]", events);
        Assert.Contains("error", events[^1], StringComparison.Ordinal);
    }

    /// <summary>The model list holds the local model and the fake provider, each with its prefix.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Models_ListsPrefixedIds()
    {
        await using var engine = new EngineFactory();
        using var client = engine.CreateClient();

        using var response = await client.GetAsync(new Uri("/v1/models", UriKind.Relative), TestContext.Current.CancellationToken);

        using var body = await ReadJsonAsync(response);
        Assert.Equal("list", body.RootElement.GetProperty("object").GetString());
        var ids = body.RootElement.GetProperty("data").EnumerateArray().Select(model => model.GetProperty("id").GetString()).ToList();
        Assert.Contains("local/ministral-3-3b", ids);
        Assert.Contains("fake/model-a", ids);
    }

    /// <summary>When keys are configured, requests without a correct key are refused on every API route.</summary>
    /// <param name="header">The Authorization header to send, if any.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer ")]
    [InlineData("Basic right-key")]
    public async Task Auth_KeysConfiguredAndWrongHeader_Returns401(string? header)
    {
        await using var engine = new EngineFactory("right-key");
        using var client = engine.CreateClient();
        if (header is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);
        }

        using var models = await client.GetAsync(new Uri("/v1/models", UriKind.Relative), TestContext.Current.CancellationToken);
        using var content = new StringContent(ValidBody, Encoding.UTF8, "application/json");
        using var chat = await client.PostAsync(new Uri("/v1/chat/completions", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, models.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, chat.StatusCode);
        Assert.Null(engine.Provider.LastRequest);
    }

    /// <summary>The correct key is accepted, and any of several keys works.</summary>
    /// <param name="key">The key to send.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("first-key")]
    [InlineData("second-key")]
    public async Task Auth_CorrectKey_Succeeds(string key)
    {
        await using var engine = new EngineFactory("first-key", "second-key");
        using var client = engine.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await client.GetAsync(new Uri("/v1/models", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A blank configured key must never let anyone in. It also does not count as protection.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Auth_OnlyBlankKeysConfigured_CountsAsNoKeys()
    {
        await using var engine = new EngineFactory(string.Empty, "  ", "real-key");
        using var client = engine.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");

        using var response = await client.GetAsync(new Uri("/v1/models", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Health needs no key, and reports degraded while the local model is not ready.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Health_LocalModelMissing_ReportsDegradedWithoutKey()
    {
        await using var engine = new EngineFactory("right-key");
        using var client = engine.CreateClient();

        // The check runs on a background service, so its first stage may still be "starting".
        string? local;
        string? status;
        var attempts = 0;
        do
        {
            using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await ReadJsonAsync(response);
            status = body.RootElement.GetProperty("status").GetString();
            local = body.RootElement.GetProperty("local").GetString();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        while (local != "missing" && ++attempts < 200);

        Assert.Equal("degraded", status);
        Assert.Equal("missing", local);
    }

    /// <summary>A request with a host name from outside the machine is refused, which blocks DNS rebinding attacks from web pages.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Hosts_NonLocalHostName_IsRefused()
    {
        await using var engine = new EngineFactory();
        using var client = engine.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/models", UriKind.Relative));
        request.Headers.Host = "evil.example.com";

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Posts a chat request.</summary>
    /// <param name="engine">The hosted engine.</param>
    /// <param name="body">The request body.</param>
    /// <returns>The response.</returns>
    private static async Task<HttpResponseMessage> PostAsync(EngineFactory engine, string body)
    {
        using var client = engine.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri("/v1/chat/completions", UriKind.Relative), content, TestContext.Current.CancellationToken);
    }

    /// <summary>Parses a JSON response.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The document.</returns>
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    /// <summary>Splits a stream into its event bodies.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The data value of each event.</returns>
    private static async Task<IReadOnlyList<string>> ReadEventsAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return [.. text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(item => item["data: ".Length..])];
    }
}

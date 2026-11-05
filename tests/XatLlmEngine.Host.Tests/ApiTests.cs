using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
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

    /// <summary>The caller's fields reach the provider unchanged, including message content the engine does not read.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Chat_ListedFields_ReachProvider()
    {
        await using var engine = new EngineFactory();
        const string Body = """
            {"model":"fake/model-a","temperature":0.5,
             "messages":[{"role":"assistant","content":"Be brief."},
                         {"role":"user","content":[{"type":"image_url","image_url":{"url":"x"}}]}]}
            """;

        using var response = await PostAsync(engine, Body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = engine.Provider.LastRequest!;
        Assert.Equal("model-a", request.Model);
        Assert.Equal(0.5, request.Body["temperature"]!.GetValue<double>());
        Assert.Equal("assistant", request.Body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("x", request.Body["messages"]![1]!["content"]![0]!["image_url"]!["url"]!.GetValue<string>());
    }

    /// <summary>Bad requests return 400 in the OpenAI error format.</summary>
    /// <param name="body">The request body.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"model":"fake/model-a","model":"fake/model-b","messages":[]}""")]
    [InlineData("""{"model":"fake/model-a","stream_options":{"include_usage":true,"include_usage":false},"messages":[]}""")]
    [InlineData("""{"model":"fake/model-a","messages":[{"role":"user","content":"\ud83d"}]}""")]
    [InlineData("""{"model":"fake/\ud83d","messages":[]}""")]
    [InlineData("""{"model":"no-slash","messages":[]}""")]
    [InlineData("""{"model":5,"messages":[]}""")]
    [InlineData("""{"messages":[]}""")]
    [InlineData("""{"model":"fake/model-a","stream":"yes","messages":[]}""")]
    public async Task Chat_BadRequest_ReturnsErrorFormat(string body)
    {
        await using var engine = new EngineFactory();

        using var response = await PostAsync(engine, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorFormatAsync(response, "invalid_request_error");
    }

    /// <summary>A field outside the provider's template returns a 400 that names it, so it is never silently ignored.</summary>
    /// <param name="field">The field's name.</param>
    /// <param name="value">The field's JSON value.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("seed", "7")]
    [InlineData("temprature", "0.5")]
    [InlineData("n", "1")]
    [InlineData("tools", "[]")]
    [InlineData("response_format", "null")]
    public async Task Chat_FieldNotInTemplate_ReturnsErrorNamingIt(string field, string value)
    {
        await using var engine = new EngineFactory();

        using var response = await PostAsync(engine, $$"""{"model":"fake/model-a","{{field}}":{{value}},"messages":[]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        Assert.Contains($"'{field}'", json.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Null(engine.Provider.LastRequest);
    }

    /// <summary>Templates are separate: the local model lists top_k and the fake provider does not.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Chat_SameField_DependsOnProvider()
    {
        await using var engine = new EngineFactory();

        using var local = await PostAsync(engine, """{"model":"local/ministral-3-3b","top_k":5,"messages":[]}""");
        using var fake = await PostAsync(engine, """{"model":"fake/model-a","top_k":5,"messages":[]}""");

        // The local model is absent in this test, so 503 shows that the request passed the template check.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, local.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, fake.StatusCode);
    }

    /// <summary>The API refuses other content types, because that blocks cross-site requests from browsers.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Chat_NotJsonContentType_Returns415()
    {
        await using var engine = new EngineFactory();
        using var client = engine.CreateClient();
        using var content = new StringContent(ValidBody, Encoding.UTF8, "text/plain");

        using var response = await client.PostAsync(new Uri("/v1/chat/completions", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("application/json", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
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
    /// <param name="retryAfter">Whether the test expects a Retry-After header.</param>
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

    /// <summary>A stream has no usage piece when the provider sent no token counts.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_NoUsageFromProvider_LeavesOutUsagePiece()
    {
        await using var engine = new EngineFactory();
        engine.Provider.Chunks = [new ChatChunk("Hi", FinishReason.Stop, null)];

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

    /// <summary>A failure in the first event is still a normal HTTP error, because the engine has not written any data.</summary>
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

    /// <summary>A route that does not exist uses the same error format as every other error.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Route_Unknown_ReturnsErrorFormat()
    {
        await using var engine = new EngineFactory();
        using var client = engine.CreateClient();

        using var response = await client.GetAsync(new Uri("/nope", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorFormatAsync(response, "not_found_error");
    }

    /// <summary>A wrong HTTP method uses the same error format as every other error.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Route_WrongMethod_ReturnsErrorFormat()
    {
        await using var engine = new EngineFactory();
        using var client = engine.CreateClient();

        using var response = await client.DeleteAsync(new Uri("/v1/models", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        await AssertErrorFormatAsync(response, "invalid_request_error");
    }

    /// <summary>Health stays 200 with the local model turned off, and says so.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Health_LocalModelDisabled_ReportsHealthyWithMessage()
    {
        await using var engine = new EngineFactory();
        var disabled = engine.WithWebHostBuilder(builder => builder.UseSetting("LocalModel:Enabled", "false"));
        using var client = disabled.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("disabled", body.RootElement.GetProperty("local").GetString());
        Assert.Contains("turned off", body.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>Health stays 200 while the local model is not ready, and explains what is wrong.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Health_LocalModelMissing_ReportsDegraded()
    {
        await using var engine = new EngineFactory();
        using var client = engine.CreateClient();

        // The check runs on a background service, so its first stage may still be "starting".
        string? local;
        string? status;
        string? message;
        var attempts = 0;
        do
        {
            using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await ReadJsonAsync(response);
            status = body.RootElement.GetProperty("status").GetString();
            local = body.RootElement.GetProperty("local").GetString();
            message = body.RootElement.GetProperty("message").GetString();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        while (local != "missing" && ++attempts < 200);

        Assert.Equal("degraded", status);
        Assert.Equal("missing", local);
        Assert.Contains("missing", message, StringComparison.Ordinal);
    }

    /// <summary>The engine refuses a request with a host name from outside the machine. This blocks DNS rebinding attacks from web pages.</summary>
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

    /// <summary>Replace returns the full prompt, and later chat requests carry it, with and without streaming.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task SystemPrompt_Replace_ReachesChatRequests()
    {
        await using var engine = new EngineFactory();
        engine.Provider.Chunks = [new ChatChunk("Hi", FinishReason.Stop, null)];

        using var replaced = await SendPromptAsync(engine, HttpMethod.Put, "/v1/system-prompts/fake", """{"prompt":"Be brief."}""");
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        using var body = await ReadJsonAsync(replaced);
        Assert.Equal("fake", body.RootElement.GetProperty("provider").GetString());
        Assert.Equal("Be brief.", body.RootElement.GetProperty("prompt").GetString());

        using var complete = await PostAsync(engine, ValidBody);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal("Be brief.", engine.Provider.LastRequest!.SystemPrompt);

        using var cleared = await SendPromptAsync(engine, HttpMethod.Put, "/v1/system-prompts/fake", """{"prompt":""}""");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using var noPrompt = await PostAsync(engine, ValidBody);
        Assert.Null(engine.Provider.LastRequest!.SystemPrompt);

        using var again = await SendPromptAsync(engine, HttpMethod.Put, "/v1/system-prompts/fake", """{"prompt":"Be kind."}""");
        using var stream = await PostAsync(engine, """{"model":"fake/model-a","stream":true,"messages":[{"role":"user","content":"Hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal("Be kind.", engine.Provider.LastRequest!.SystemPrompt);
    }

    /// <summary>Append adds to the stored prompt, and a newline separates the parts.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task SystemPrompt_Append_AddsToEnd()
    {
        await using var engine = new EngineFactory();

        using var first = await SendPromptAsync(engine, HttpMethod.Post, "/v1/system-prompts/fake/append", """{"prompt":"One."}""");
        using var second = await SendPromptAsync(engine, HttpMethod.Post, "/v1/system-prompts/fake/append", """{"prompt":"Two."}""");

        using var firstBody = await ReadJsonAsync(first);
        using var secondBody = await ReadJsonAsync(second);
        Assert.Equal("One.", firstBody.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("One.\nTwo.", secondBody.RootElement.GetProperty("prompt").GetString());
    }

    /// <summary>A prompt in the configuration is the starting value, and the local provider exposes it.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task SystemPrompt_Configured_IsStartValue()
    {
        await using var engine = new EngineFactory();
        await using var configured = engine.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["LocalModel:SystemPrompt"] = "Configured." })));
        using var client = configured.CreateClient();
        using var content = new StringContent("""{"prompt":"Added."}""", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(new Uri("/v1/system-prompts/local/append", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("Configured.\nAdded.", body.RootElement.GetProperty("prompt").GetString());
    }

    /// <summary>An unknown provider returns 404 from both routes.</summary>
    /// <param name="route">The route.</param>
    /// <param name="method">The HTTP method.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("/v1/system-prompts/nobody", "PUT")]
    [InlineData("/v1/system-prompts/nobody/append", "POST")]
    public async Task SystemPrompt_UnknownProvider_Returns404(string route, string method)
    {
        await using var engine = new EngineFactory();

        using var response = await SendPromptAsync(engine, new HttpMethod(method), route, """{"prompt":"x"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorFormatAsync(response, "not_found_error");
    }

    /// <summary>A bad body returns 400 and leaves the prompt unchanged.</summary>
    /// <param name="body">The request body.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"prompt":null}""")]
    [InlineData("""{"prompt":5}""")]
    public async Task SystemPrompt_BadBody_Returns400(string body)
    {
        await using var engine = new EngineFactory();

        using var response = await SendPromptAsync(engine, HttpMethod.Put, "/v1/system-prompts/fake", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorFormatAsync(response, "invalid_request_error");
        using var chat = await PostAsync(engine, ValidBody);
        Assert.Null(engine.Provider.LastRequest!.SystemPrompt);
    }

    /// <summary>A body that is not JSON returns 415, because browsers can send such a body from any site.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task SystemPrompt_NotJson_Returns415()
    {
        await using var engine = new EngineFactory();
        using var client = engine.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/v1/system-prompts/fake")
        {
            Content = new StringContent("""{"prompt":"x"}""", Encoding.UTF8, "text/plain"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    /// <summary>A chat request with its own system prompt returns 400 and does not reach the provider.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Chat_OwnSystemPrompt_Returns400()
    {
        await using var engine = new EngineFactory();

        using var response = await PostAsync(engine, """{"model":"fake/model-a","messages":[{"role":"system","content":"x"},{"role":"user","content":"Hi"}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorFormatAsync(response, "invalid_request_error");
        Assert.Null(engine.Provider.LastRequest);
    }

    /// <summary>Sends a JSON body to a system prompt route.</summary>
    /// <param name="engine">The engine.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="route">The route.</param>
    /// <param name="body">The request body.</param>
    /// <returns>The response.</returns>
    private static async Task<HttpResponseMessage> SendPromptAsync(EngineFactory engine, HttpMethod method, string route, string body)
    {
        using var client = engine.CreateClient();
        using var request = new HttpRequestMessage(method, route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Checks that a response has the one error format the API uses.</summary>
    /// <param name="response">The response.</param>
    /// <param name="type">The expected error category.</param>
    /// <returns>A task for the check.</returns>
    private static async Task AssertErrorFormatAsync(HttpResponseMessage response, string type)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        using var json = await ReadJsonAsync(response);
        var error = json.RootElement.GetProperty("error");
        Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));
        Assert.Equal(type, error.GetProperty("type").GetString());
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

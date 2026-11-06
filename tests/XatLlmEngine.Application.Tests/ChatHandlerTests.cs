using System.Text.Json.Nodes;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.ListModels;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Tests;

/// <summary>Tests chat routing, validation and model listing.</summary>
public class ChatHandlerTests
{
    /// <summary>Builds a minimal valid request body.</summary>
    /// <returns>The body.</returns>
    private static JsonObject Hello() => new() { ["messages"] = new JsonArray() };

    /// <summary>The provider prefix selects the provider, which receives the name without the prefix.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_KnownProvider_RoutesWithoutPrefix()
    {
        var chosen = new FakeChatProvider("chosen");
        var handler = new CompleteChatHandler(new ProviderRegistry([new FakeChatProvider("other"), chosen]));

        var result = await handler.HandleAsync(new CompleteChatCommand("chosen/some/model", Hello()), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("some/model", chosen.LastRequest!.Model);
        Assert.Equal("chosen/some/model", result.Value.Model);
    }

    /// <summary>Provider names ignore case, like configuration keys.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ProviderNameInOtherCase_StillRoutes()
    {
        var handler = new CompleteChatHandler(new ProviderRegistry([new FakeChatProvider("OpenAI")]));

        var result = await handler.HandleAsync(new CompleteChatCommand("openai/model", Hello()), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    /// <summary>The handler reports an unknown provider as not found.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_UnknownProvider_FailsAsNotFound()
    {
        var handler = new CompleteChatHandler(new ProviderRegistry([new FakeChatProvider("known")]));

        var result = await handler.HandleAsync(new CompleteChatCommand("unknown/model", Hello()), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
    }

    /// <summary>A provider's failure reaches the caller unchanged.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ProviderFails_ReturnsThatFailure()
    {
        var failure = new EngineError(ErrorKind.RateLimited, "Slow down.");
        var handler = new StreamChatHandler(new ProviderRegistry([new FakeChatProvider("cloud") { Failure = failure }]));

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", Hello()), TestContext.Current.CancellationToken);

        Assert.Equal(failure, result.Error);
    }

    /// <summary>The handler rejects a field outside the provider's template by name, before it calls the provider.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_FieldNotInTemplate_FailsAndNamesIt()
    {
        var provider = new FakeChatProvider("cloud");
        var handler = new CompleteChatHandler(new ProviderRegistry([provider]));
        var body = Hello();
        body["seed"] = 7;

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", body), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error!.Kind);
        Assert.Contains("'seed'", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("messages", result.Error.Message, StringComparison.Ordinal);
        Assert.Null(provider.LastRequest);
    }

    /// <summary>Each provider has its own template, so one field can be valid for one provider and not for another.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_SameField_DependsOnProvider()
    {
        var handler = new CompleteChatHandler(new ProviderRegistry(
        [
            new FakeChatProvider("with") { RequestFields = ["messages", "seed"] },
            new FakeChatProvider("without"),
        ]));
        var body = Hello();
        body["seed"] = 7;

        var accepted = await handler.HandleAsync(new CompleteChatCommand("with/model", body), TestContext.Current.CancellationToken);
        var rejected = await handler.HandleAsync(new CompleteChatCommand("without/model", body), TestContext.Current.CancellationToken);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(ErrorKind.InvalidRequest, rejected.Error!.Kind);
    }

    /// <summary>The engine needs the model and stream fields for routing, so a template does not have to list them.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ModelAndStream_AreAlwaysAccepted()
    {
        var handler = new CompleteChatHandler(new ProviderRegistry([new FakeChatProvider("cloud")]));
        var body = Hello();
        body["model"] = "cloud/model";
        body["stream"] = false;

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", body), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    /// <summary>The handler lists every model with its provider prefix, so that a caller can send the same text back.</summary>
    [Fact]
    public void ListModels_ProvidersWithModels_ListsWithPrefix()
    {
        var handler = new ListModelsHandler(new ProviderRegistry(
        [
            new FakeChatProvider("local") { Models = ["small"] },
            new FakeChatProvider("cloud") { Models = ["a", "b"] },
        ]));

        Assert.Equal(["local/small", "cloud/a", "cloud/b"], handler.Handle().Select(entry => entry.Id));
        Assert.Equal(["local", "cloud", "cloud"], handler.Handle().Select(entry => entry.Provider));
    }

    /// <summary>The provider gets its own copy of the body, so a change by the provider never reaches the caller's body.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ProviderChangesBody_CallerBodyStays()
    {
        var provider = new FakeChatProvider("cloud") { ChangeBody = true };
        var handler = new CompleteChatHandler(new ProviderRegistry([provider]));
        var body = Hello();

        await handler.HandleAsync(new CompleteChatCommand("cloud/model", body), TestContext.Current.CancellationToken);

        Assert.False(body.ContainsKey("added"));
        Assert.True(provider.LastRequest!.Body.ContainsKey("added"));
    }

    /// <summary>Tool content inside a message is refused for every provider, before the provider is called.</summary>
    /// <param name="message">The message JSON.</param>
    /// <param name="expected">Text that the message must contain.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("""{"role":"tool","tool_call_id":"1","content":"42"}""", "role 'tool'")]
    [InlineData("""{"role":"function","name":"f","content":"42"}""", "role 'function'")]
    [InlineData("""{"role":"assistant","content":null,"tool_calls":[{"id":"1","type":"function","function":{"name":"f","arguments":"{}"}}]}""", "field 'tool_calls'")]
    [InlineData("""{"role":"assistant","content":null,"function_call":{"name":"f","arguments":"{}"}}""", "field 'function_call'")]
    [InlineData("""{"role":"assistant","content":[{"type":"tool_use","id":"1","name":"f","input":{}}]}""", "type 'tool_use'")]
    [InlineData("""{"role":"user","content":[{"type":"tool_result","tool_use_id":"1","content":"42"}]}""", "type 'tool_result'")]
    [InlineData("""{"role":"user","content":[{"type":"web_search_tool_result","tool_use_id":"1","content":[]}]}""", "type 'web_search_tool_result'")]
    public async Task Complete_ToolContent_FailsBeforeProvider(string message, string expected)
    {
        var provider = new FakeChatProvider("cloud");
        var handler = new CompleteChatHandler(new ProviderRegistry([provider]));
        var body = new JsonObject { ["messages"] = new JsonArray(JsonNode.Parse("""{"role":"user","content":"Hi"}"""), JsonNode.Parse(message)) };

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", body), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error!.Kind);
        Assert.Contains("Message 1", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, result.Error.Message, StringComparison.Ordinal);
        Assert.Null(provider.LastRequest);
    }

    /// <summary>Text and image content still pass, because the engine forwards images unchanged.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ImageContent_Passes()
    {
        var handler = new CompleteChatHandler(new ProviderRegistry([new FakeChatProvider("cloud")]));
        var body = new JsonObject
        {
            ["messages"] = JsonNode.Parse("""[{"role":"user","content":[{"type":"text","text":"What is this?"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AA=="}}]},{"role":"assistant","content":"A cat."}]"""),
        };

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", body), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    /// <summary>Two providers with the same name stop the engine at startup.</summary>
    [Fact]
    public void Registry_DuplicateNames_Throws()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => new ProviderRegistry([new FakeChatProvider("cloud"), new FakeChatProvider("CLOUD")]));

        Assert.Contains("cloud", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A stream with no piece fails before the success status.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_NoPiece_FailsAsUpstream()
    {
        var handler = new StreamChatHandler(new ProviderRegistry([new FakeChatProvider("cloud") { Chunks = [] }]));

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", Hello()), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Upstream, result.Error!.Kind);
    }

    /// <summary>A failure in the first piece fails before the success status.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_FailsAtFirstPiece_ReturnsFailure()
    {
        var failure = new EngineError(ErrorKind.Unavailable, "Gone.");
        var handler = new StreamChatHandler(new ProviderRegistry([new FakeChatProvider("cloud") { Chunks = [], StreamFailure = failure }]));

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", Hello()), TestContext.Current.CancellationToken);

        Assert.Equal(failure, result.Error);
    }

    /// <summary>A stream that ends with no finish reason fails part-way, because the answer can be incomplete.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_NoFinishReason_Throws()
    {
        var handler = new StreamChatHandler(new ProviderRegistry([new FakeChatProvider("cloud") { Chunks = [new ChatChunk("Hi", null, null)] }]));
        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", Hello()), TestContext.Current.CancellationToken);
        await using var answer = result.Value!;

        var failure = await Assert.ThrowsAsync<ChatStreamException>(async () =>
        {
            await foreach (var _ in answer.ReadAsync())
            {
            }
        });

        Assert.Equal(ErrorKind.Upstream, failure.Error.Kind);
    }

    /// <summary>The stream gives text and finish pieces, and keeps the token counts apart, from any piece.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_Pieces_KeepsUsageApart()
    {
        var chunks = new[]
        {
            new ChatChunk("Hi", null, null),
            new ChatChunk(null, null, new TokenUsage(3, 1)),
            new ChatChunk(" there", FinishReason.Stop, new TokenUsage(3, 2)),
        };
        var handler = new StreamChatHandler(new ProviderRegistry([new FakeChatProvider("cloud") { Chunks = chunks }]));
        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", Hello()), TestContext.Current.CancellationToken);
        await using var answer = result.Value!;

        var pieces = new List<ChatChunk>();
        await foreach (var piece in answer.ReadAsync())
        {
            pieces.Add(piece);
        }

        Assert.Equal([new ChatChunk("Hi", null, null), new ChatChunk(" there", FinishReason.Stop, null)], pieces);
        Assert.Equal(new TokenUsage(3, 2), answer.Usage);
        Assert.Equal("cloud/model", answer.Model);
    }
}

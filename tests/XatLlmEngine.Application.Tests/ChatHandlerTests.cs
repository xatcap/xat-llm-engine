using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Features.ListModels;
using XatLlmEngine.Application.Features.SystemPrompts;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Tests;

/// <summary>Tests chat routing, validation and model listing.</summary>
public class ChatHandlerTests
{
    /// <summary>Builds a minimal valid request body.</summary>
    /// <returns>The body.</returns>
    private static JsonObject Hello() => new() { ["messages"] = new JsonArray() };

    /// <summary>Builds the use case for complete answers.</summary>
    /// <param name="providers">The enabled providers.</param>
    /// <returns>The use case.</returns>
    private static CompleteChatHandler Complete(params IChatProvider[] providers) => new(providers, new SystemPromptStore(providers));

    /// <summary>Builds the use case for streamed answers.</summary>
    /// <param name="providers">The enabled providers.</param>
    /// <returns>The use case.</returns>
    private static StreamChatHandler Stream(params IChatProvider[] providers) => new(providers, new SystemPromptStore(providers));

    /// <summary>The provider prefix selects the provider, which receives the name without the prefix.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_KnownProvider_RoutesWithoutPrefix()
    {
        var chosen = new FakeChatProvider("chosen");
        var handler = Complete(new FakeChatProvider("other"), chosen);

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
        var handler = Complete(new FakeChatProvider("OpenAI"));

        var result = await handler.HandleAsync(new CompleteChatCommand("openai/model", Hello()), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    /// <summary>The handler reports an unknown provider as not found.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_UnknownProvider_FailsAsNotFound()
    {
        var handler = Complete(new FakeChatProvider("known"));

        var result = await handler.HandleAsync(new CompleteChatCommand("unknown/model", Hello()), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
    }

    /// <summary>A provider's failure reaches the caller unchanged.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ProviderFails_ReturnsThatFailure()
    {
        var failure = new EngineError(ErrorKind.RateLimited, "Slow down.");
        var handler = Stream(new FakeChatProvider("cloud") { Failure = failure });

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", Hello()), TestContext.Current.CancellationToken);

        Assert.Equal(failure, result.Error);
    }

    /// <summary>The handler rejects a field outside the provider's template by name, before it calls the provider.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_FieldNotInTemplate_FailsAndNamesIt()
    {
        var provider = new FakeChatProvider("cloud");
        var handler = Complete(provider);
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
        var handler = Complete(
            new FakeChatProvider("with") { RequestFields = ["messages", "seed"] },
            new FakeChatProvider("without"));
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
        var handler = Complete(new FakeChatProvider("cloud"));
        var body = Hello();
        body["model"] = "cloud/model";
        body["stream"] = false;

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", body), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    /// <summary>A request cannot set its own system prompt, with or without a stored prompt. Case differences do not avoid the check.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="configuredPrompt">The provider's stored prompt.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("""{"system":"x","messages":[]}""", null)]
    [InlineData("""{"system":null,"messages":[]}""", null)]
    [InlineData("""{"messages":[{"role":"system","content":"x"}]}""", null)]
    [InlineData("""{"messages":[{"role":"developer","content":"x"}]}""", null)]
    [InlineData("""{"messages":[{"role":"System","content":"x"}]}""", null)]
    [InlineData("""{"messages":[{"role":"user","content":"a"},{"role":"system","content":"x"}]}""", null)]
    [InlineData("""{"messages":[{"role":"system","content":"x"}]}""", "stored")]
    public async Task Complete_OwnSystemPrompt_Fails(string body, string? configuredPrompt)
    {
        var provider = new FakeChatProvider("cloud") { RequestFields = ["messages", "system"], SystemPrompt = configuredPrompt };

        var result = await Complete(provider).HandleAsync(new CompleteChatCommand("cloud/model", JsonNode.Parse(body)!.AsObject()), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error!.Kind);
        Assert.Contains("/v1/system-prompts/cloud", result.Error.Message, StringComparison.Ordinal);
        Assert.Null(provider.LastRequest);
    }

    /// <summary>A message with an unusual role goes to the provider, which decides whether that role is valid.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_NonTextRole_ReachesProvider()
    {
        var provider = new FakeChatProvider("cloud");
        var body = JsonNode.Parse("""{"messages":[{"role":5,"content":"x"},"text"]}""")!.AsObject();

        var result = await Complete(provider).HandleAsync(new CompleteChatCommand("cloud/model", body), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    /// <summary>The router gives the provider its current stored prompt, for both answer types.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Handlers_StoredPrompt_ReachesProvider()
    {
        var provider = new FakeChatProvider("cloud");
        IChatProvider[] providers = [provider];
        var store = new SystemPromptStore(providers);
        store.Replace("cloud", "Be brief.");

        await new CompleteChatHandler(providers, store).HandleAsync(new CompleteChatCommand("cloud/model", Hello()), TestContext.Current.CancellationToken);
        var completePrompt = provider.LastRequest!.SystemPrompt;
        store.Replace("cloud", "Be kind.");
        await new StreamChatHandler(providers, store).HandleAsync(new CompleteChatCommand("cloud/model", Hello()), TestContext.Current.CancellationToken);

        Assert.Equal("Be brief.", completePrompt);
        Assert.Equal("Be kind.", provider.LastRequest!.SystemPrompt);
    }

    /// <summary>The handler lists every model with its provider prefix, so that a caller can send the same text back.</summary>
    [Fact]
    public void ListModels_ProvidersWithModels_ListsWithPrefix()
    {
        var handler = new ListModelsHandler(
        [
            new FakeChatProvider("local") { Models = ["small"] },
            new FakeChatProvider("cloud") { Models = ["a", "b"] },
        ]);

        Assert.Equal(["local/small", "cloud/a", "cloud/b"], handler.Handle());
    }
}

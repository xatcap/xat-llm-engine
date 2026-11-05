using System.Text.Json.Nodes;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Features.ListModels;
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
        var handler = new CompleteChatHandler([new FakeChatProvider("other"), chosen]);

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
        var handler = new CompleteChatHandler([new FakeChatProvider("OpenAI")]);

        var result = await handler.HandleAsync(new CompleteChatCommand("openai/model", Hello()), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    /// <summary>An unknown provider is reported as not found.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_UnknownProvider_FailsAsNotFound()
    {
        var handler = new CompleteChatHandler([new FakeChatProvider("known")]);

        var result = await handler.HandleAsync(new CompleteChatCommand("unknown/model", Hello()), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
    }

    /// <summary>A provider's failure reaches the caller unchanged.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ProviderFails_ReturnsThatFailure()
    {
        var failure = new EngineError(ErrorKind.RateLimited, "Slow down.");
        var handler = new StreamChatHandler([new FakeChatProvider("cloud") { Failure = failure }]);

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", Hello()), TestContext.Current.CancellationToken);

        Assert.Equal(failure, result.Error);
    }

    /// <summary>A field outside the provider's template is rejected by name, before the provider is called.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_FieldNotInTemplate_FailsAndNamesIt()
    {
        var provider = new FakeChatProvider("cloud");
        var handler = new CompleteChatHandler([provider]);
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
        var handler = new CompleteChatHandler(
        [
            new FakeChatProvider("with") { RequestFields = ["messages", "seed"] },
            new FakeChatProvider("without"),
        ]);
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
        var handler = new CompleteChatHandler([new FakeChatProvider("cloud")]);
        var body = Hello();
        body["model"] = "cloud/model";
        body["stream"] = false;

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", body), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    /// <summary>Every model is listed with its provider prefix, so it can be sent back as written.</summary>
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

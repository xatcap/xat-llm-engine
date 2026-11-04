using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Features.ListModels;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Tests;

/// <summary>Tests chat routing, validation and model listing.</summary>
public class ChatHandlerTests
{
    /// <summary>A minimal valid conversation.</summary>
    private static readonly ChatMessage[] Hello = [new ChatMessage(ChatRole.User, "Hello")];

    /// <summary>The provider prefix selects the provider, which receives the name without the prefix.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_KnownProvider_RoutesWithoutPrefix()
    {
        var chosen = new FakeChatProvider("chosen");
        var handler = new CompleteChatHandler([new FakeChatProvider("other"), chosen]);

        var result = await handler.HandleAsync(new CompleteChatCommand("chosen/some/model", Hello), TestContext.Current.CancellationToken);

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

        var result = await handler.HandleAsync(new CompleteChatCommand("openai/model", Hello), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    /// <summary>An unknown provider is reported as not found.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_UnknownProvider_FailsAsNotFound()
    {
        var handler = new CompleteChatHandler([new FakeChatProvider("known")]);

        var result = await handler.HandleAsync(new CompleteChatCommand("unknown/model", Hello), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
    }

    /// <summary>A provider's failure reaches the caller unchanged.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ProviderFails_ReturnsThatFailure()
    {
        var failure = new EngineError(ErrorKind.RateLimited, "Slow down.");
        var handler = new StreamChatHandler([new FakeChatProvider("cloud") { Failure = failure }]);

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", Hello), TestContext.Current.CancellationToken);

        Assert.Equal(failure, result.Error);
    }

    /// <summary>Invalid values are rejected before any provider is called.</summary>
    /// <param name="temperature">The temperature to send.</param>
    /// <param name="topP">The top_p value to send.</param>
    /// <param name="maxTokens">The token limit to send.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData(-0.1, null, null)]
    [InlineData(null, 1.5, null)]
    [InlineData(null, -0.5, null)]
    [InlineData(null, null, 0)]
    public async Task Complete_ValueOutOfRange_FailsWithoutCallingProvider(double? temperature, double? topP, int? maxTokens)
    {
        var provider = new FakeChatProvider("cloud");
        var handler = new CompleteChatHandler([provider]);

        var result = await handler.HandleAsync(
            new CompleteChatCommand("cloud/model", Hello, temperature, topP, maxTokens),
            TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error!.Kind);
        Assert.Null(provider.LastRequest);
    }

    /// <summary>A request with no messages is invalid.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_NoMessages_FailsAsInvalidRequest()
    {
        var handler = new CompleteChatHandler([new FakeChatProvider("cloud")]);

        var result = await handler.HandleAsync(new CompleteChatCommand("cloud/model", []), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error!.Kind);
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

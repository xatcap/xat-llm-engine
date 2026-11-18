using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Features.ExpandQuery;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Tests;

/// <summary>Tests how the handler turns one query into several.</summary>
public class ExpandQueryHandlerTests
{
    /// <summary>The handler removes numbers, bullets, quotes and blank lines.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Handle_MessyAnswer_CleansEachLine()
    {
        var (handler, _) = Create("1. first query\n2) second query\n\n- third query\n* \"fourth query\"\n• `fifth query`");

        var result = await handler.HandleAsync(new ExpandQueryCommand("original", 5), TestContext.Current.CancellationToken);

        Assert.Equal(["first query", "second query", "third query", "fourth query", "fifth query"], result.Value!.Queries);
    }

    /// <summary>The handler keeps only the number of queries that the caller asked for.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Handle_MoreLinesThanCount_KeepsFirstCount()
    {
        var (handler, _) = Create("a\nb\nc\nd");

        var result = await handler.HandleAsync(new ExpandQueryCommand("original", 2), TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], result.Value!.Queries);
    }

    /// <summary>The handler drops repeats, ignoring case, and drops a copy of the original query.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Handle_RepeatedLines_DropsRepeatsAndOriginal()
    {
        var (handler, _) = Create("Original Query\nalpha\nALPHA\nbeta\nalpha");

        var result = await handler.HandleAsync(new ExpandQueryCommand("original query", 5), TestContext.Current.CancellationToken);

        Assert.Equal(["alpha", "beta"], result.Value!.Queries);
    }

    /// <summary>The handler removes the thinking block that some models write first, also when the block never ends.</summary>
    /// <param name="answer">The text of the answer.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("<think>\nI should write two queries.\n</think>\nalpha\nbeta")]
    [InlineData("<think>reasoning</think>alpha\nbeta")]
    public async Task Handle_ThinkingBlock_IsRemoved(string answer)
    {
        var (handler, _) = Create(answer);

        var result = await handler.HandleAsync(new ExpandQueryCommand("original", 5), TestContext.Current.CancellationToken);

        Assert.Equal(["alpha", "beta"], result.Value!.Queries);
    }

    /// <summary>The handler drops an introduction line that ends with a colon.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Handle_IntroductionLine_IsDropped()
    {
        var (handler, _) = Create("Here are 2 queries:\nalpha\nbeta");

        var result = await handler.HandleAsync(new ExpandQueryCommand("original", 5), TestContext.Current.CancellationToken);

        Assert.Equal(["alpha", "beta"], result.Value!.Queries);
    }

    /// <summary>When the model stops at the token limit, the last line can be incomplete, so the handler drops it.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Handle_AnswerCutOff_DropsLastLine()
    {
        var provider = new FakeChatProvider(ProviderNames.QueryExpansion) { Models = ["small"], Answer = "alpha\nbeta\ngam", Finish = FinishReason.Length };
        var registry = new ProviderRegistry([provider]);
        var handler = new ExpandQueryHandler(registry, new CompleteChatHandler(registry));

        var result = await handler.HandleAsync(new ExpandQueryCommand("original", 5), TestContext.Current.CancellationToken);

        Assert.Equal(["alpha", "beta"], result.Value!.Queries);
    }

    /// <summary>An answer with no new query is a failure of the model, not an empty list.</summary>
    /// <param name="answer">The text of the answer.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("")]
    [InlineData("  \n  ")]
    [InlineData("original")]
    [InlineData("<think>unfinished")]
    public async Task Handle_NoNewQuery_FailsAsUpstream(string answer)
    {
        var (handler, _) = Create(answer);

        var result = await handler.HandleAsync(new ExpandQueryCommand("original", 3), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Upstream, result.Error?.Kind);
    }

    /// <summary>A query or a count outside the limits fails before the model runs.</summary>
    /// <param name="query">The query.</param>
    /// <param name="count">The count.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("", 3)]
    [InlineData("   ", 3)]
    [InlineData("query", 0)]
    [InlineData("query", ExpandQueryHandler.MaxCount + 1)]
    public async Task Handle_InvalidInput_FailsWithoutCallingModel(string query, int count)
    {
        var (handler, provider) = Create("alpha");

        var result = await handler.HandleAsync(new ExpandQueryCommand(query, count), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error?.Kind);
        Assert.Null(provider.LastRequest);
    }

    /// <summary>A query that is longer than the limit fails.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Handle_QueryTooLong_Fails()
    {
        var (handler, _) = Create("alpha");

        var result = await handler.HandleAsync(new ExpandQueryCommand(new string('x', ExpandQueryHandler.MaxQueryLength + 1), 3), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error?.Kind);
    }

    /// <summary>When the model is not registered, the handler tells the caller how to turn it on.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Handle_ModelNotRegistered_FailsAsUnavailable()
    {
        var registry = new ProviderRegistry([new FakeChatProvider(ProviderNames.Local) { Models = ["main"] }]);
        var handler = new ExpandQueryHandler(registry, new CompleteChatHandler(registry));

        var result = await handler.HandleAsync(new ExpandQueryCommand("query", 3), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error?.Kind);
        Assert.Contains("QueryExpansionModel:Enabled", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A failure of the model reaches the caller unchanged.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Handle_ModelFails_ReturnsThatFailure()
    {
        var failure = new EngineError(ErrorKind.Overloaded, "Busy.");
        var (handler, _) = Create("alpha", failure);

        var result = await handler.HandleAsync(new ExpandQueryCommand("query", 3), TestContext.Current.CancellationToken);

        Assert.Equal(failure, result.Error);
    }

    /// <summary>The handler writes the prompt, trims the query, and asks the right model.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Handle_ValidInput_SendsPromptToQueryExpansionModel()
    {
        var (handler, provider) = Create("alpha");

        var result = await handler.HandleAsync(new ExpandQueryCommand("  my query  ", 4), TestContext.Current.CancellationToken);

        Assert.Equal("query-expansion/small", result.Value!.Model);
        Assert.Equal("small", provider.LastRequest!.Model);
        var messages = provider.LastRequest.Body["messages"]!.AsArray();
        Assert.Contains("4 different", messages[0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("my query", messages[1]!["content"]!.GetValue<string>());
    }

    /// <summary>Builds a handler over a fake query-expansion model.</summary>
    /// <param name="answer">The text that the model writes.</param>
    /// <param name="failure">The failure that the model returns, or <see langword="null"/> for none.</param>
    /// <returns>The handler and the fake model.</returns>
    private static (ExpandQueryHandler Handler, FakeChatProvider Provider) Create(string answer, EngineError? failure = null)
    {
        var provider = new FakeChatProvider(ProviderNames.QueryExpansion) { Models = ["small"], Answer = answer, Failure = failure };
        var registry = new ProviderRegistry([provider]);
        return (new ExpandQueryHandler(registry, new CompleteChatHandler(registry)), provider);
    }
}

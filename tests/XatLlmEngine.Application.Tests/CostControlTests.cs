using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Tests;

/// <summary>Tests the output token limit and stream usage forwarding.</summary>
public class CostControlTests
{
    /// <summary>A request that asks for more tokens than the limit gets 400, and the provider is not called.</summary>
    /// <param name="field">The token field of the template.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("max_tokens")]
    [InlineData("max_completion_tokens")]
    public async Task Limit_AskedAboveLimit_FailsBeforeProvider(string field)
    {
        var provider = new FakeChatProvider("local") { RequestFields = ["messages", field], MaxTokensLimit = 100 };
        var body = new JsonObject { ["messages"] = new JsonArray(), [field] = 101 };

        var result = await new CompleteChatHandler(new ProviderRegistry([provider])).HandleAsync(new("local/m", body), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error?.Kind);
        Assert.Contains($"'{field}'", result.Error?.Message, StringComparison.Ordinal);
        Assert.Null(provider.LastRequest);
    }

    /// <summary>A token value that is not a whole number of at least 1 gets 400, because it could pass the limit. llama-server reads -1 as no limit.</summary>
    /// <param name="value">The JSON value of the token field.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("\"9999\"")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("null")]
    public async Task Limit_InvalidValue_Fails(string value)
    {
        var provider = new FakeChatProvider("local") { RequestFields = ["messages", "max_tokens"], MaxTokensLimit = 100 };
        var body = new JsonObject { ["messages"] = new JsonArray(), ["max_tokens"] = JsonNode.Parse(value) };

        var result = await new CompleteChatHandler(new ProviderRegistry([provider])).HandleAsync(new("local/m", body), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error?.Kind);
        Assert.Null(provider.LastRequest);
    }

    /// <summary>A request with no token field gets the limit, in the field that the template lists. A value at or below the limit stays.</summary>
    /// <param name="sent">The value the caller sends, or <see langword="null"/> for none.</param>
    /// <param name="expected">The value the provider must get.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData(null, 100)]
    [InlineData(40, 40)]
    [InlineData(100, 100)]
    public async Task Limit_WithinLimit_ProviderGetsValue(int? sent, int expected)
    {
        var provider = new FakeChatProvider("local") { RequestFields = ["messages", "max_completion_tokens"], MaxTokensLimit = 100 };
        var body = new JsonObject { ["messages"] = new JsonArray() };
        if (sent is not null)
        {
            body["max_completion_tokens"] = sent;
        }

        await new CompleteChatHandler(new ProviderRegistry([provider])).HandleAsync(new("local/m", body), TestContext.Current.CancellationToken);

        Assert.Equal(expected, provider.LastRequest!.Body["max_completion_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_completion_tokens") && sent is null);
    }

    /// <summary>Without a limit, the engine adds no token field.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Limit_None_AddsNothing()
    {
        var provider = new FakeChatProvider("local") { RequestFields = ["messages", "max_tokens"] };

        await new CompleteChatHandler(new ProviderRegistry([provider])).HandleAsync(new("local/m", new JsonObject { ["messages"] = new JsonArray() }), TestContext.Current.CancellationToken);

        Assert.False(provider.LastRequest!.Body.ContainsKey("max_tokens"));
    }

    /// <summary>The stream handler forwards token counts only when the provider allows it for the request.</summary>
    /// <param name="forwardUsage">Whether the provider tells the stream to forward token counts.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_ForwardUsage_FollowsProvider(bool forwardUsage)
    {
        var provider = new FakeChatProvider("local") { ForwardStreamUsage = forwardUsage, Chunks = [new ChatChunk("Hi", FinishReason.Stop, new TokenUsage(1, 1))] };
        var body = new JsonObject { ["messages"] = new JsonArray() };

        var result = await new StreamChatHandler(new ProviderRegistry([provider])).HandleAsync(new("local/m", body), TestContext.Current.CancellationToken);
        await using var answer = result.Value!;

        Assert.Equal(forwardUsage, answer.ForwardUsage);
        Assert.Same(body, provider.LastStreamUsageBody);
    }
}

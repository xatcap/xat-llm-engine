using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Features.Health;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Tests;

/// <summary>Tests the token limit, the token budget and the estimate of tokens.</summary>
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
        var provider = new FakeChatProvider("cloud") { RequestFields = ["messages", field], MaxTokensLimit = 100 };
        var body = new JsonObject { ["messages"] = new JsonArray(), [field] = 101 };

        var result = await new CompleteChatHandler(new ProviderRegistry([provider])).HandleAsync(new("cloud/m", body), TestContext.Current.CancellationToken);

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
        var provider = new FakeChatProvider("cloud") { RequestFields = ["messages", "max_tokens"], MaxTokensLimit = 100 };
        var body = new JsonObject { ["messages"] = new JsonArray(), ["max_tokens"] = JsonNode.Parse(value) };

        var result = await new CompleteChatHandler(new ProviderRegistry([provider])).HandleAsync(new("cloud/m", body), TestContext.Current.CancellationToken);

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
        var provider = new FakeChatProvider("cloud") { RequestFields = ["messages", "max_completion_tokens"], MaxTokensLimit = 100 };
        var body = new JsonObject { ["messages"] = new JsonArray() };
        if (sent is not null)
        {
            body["max_completion_tokens"] = sent;
        }

        await new CompleteChatHandler(new ProviderRegistry([provider])).HandleAsync(new("cloud/m", body), TestContext.Current.CancellationToken);

        Assert.Equal(expected, provider.LastRequest!.Body["max_completion_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_completion_tokens") && sent is null);
    }

    /// <summary>Without a limit, the engine adds no token field.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Limit_None_AddsNothing()
    {
        var provider = new FakeChatProvider("cloud") { RequestFields = ["messages", "max_tokens"] };

        await new CompleteChatHandler(new ProviderRegistry([provider])).HandleAsync(new("cloud/m", new JsonObject { ["messages"] = new JsonArray() }), TestContext.Current.CancellationToken);

        Assert.False(provider.LastRequest!.Body.ContainsKey("max_tokens"));
    }

    /// <summary>A provider that has used its budget gets 429, with the wait until enough tokens leave the period. The provider is not called.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Budget_UsedUp_FailsWithWait()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-06T10:30:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var ledger = new MemoryLedger();
        ledger.Record("cloud", 1000, time.GetUtcNow());
        var provider = new FakeChatProvider("cloud") { Budget = new TokenBudget(1000, TimeSpan.FromHours(24)) };

        var result = await new CompleteChatHandler(new ProviderRegistry([provider]), ledger: ledger, timeProvider: time).HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.BudgetExceeded, result.Error?.Kind);
        Assert.Equal(TimeSpan.FromHours(24), result.Error?.RetryAfter);
        Assert.Null(provider.LastRequest);
    }

    /// <summary>Each answer counts against the budget. The engine estimates the tokens when the provider does not report them.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Budget_AnswerWithoutUsage_RecordsEstimate()
    {
        var ledger = new MemoryLedger();
        var provider = new FakeChatProvider("cloud") { Budget = new TokenBudget(1_000_000, TimeSpan.FromHours(1)) };

        await new CompleteChatHandler(new ProviderRegistry([provider]), ledger: ledger).HandleAsync(Command(), TestContext.Current.CancellationToken);

        // The answer "answer" is 6 bytes, so 2 tokens. The messages are a small JSON array.
        Assert.True(ledger.Total("cloud") > 2);
    }

    /// <summary>A stream that the caller stops counts the tokens that arrived.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Budget_StreamCanceled_RecordsTokens()
    {
        var ledger = new MemoryLedger();
        var provider = new FakeChatProvider("cloud")
        {
            Budget = new TokenBudget(1_000_000, TimeSpan.FromHours(1)),
            Chunks = [new ChatChunk("Hi", null, new TokenUsage(10, 1)), new ChatChunk(" there", FinishReason.Stop, null)],
        };
        var result = await new StreamChatHandler(new ProviderRegistry([provider]), ledger: ledger).HandleAsync(Command(), TestContext.Current.CancellationToken);

        await using (var answer = result.Value!)
        {
            await foreach (var _ in answer.ReadAsync())
            {
                break;
            }
        }

        Assert.Equal(11, ledger.Total("cloud"));
    }

    /// <summary>The health of a provider that has used its budget says so, and the provider cannot take requests.</summary>
    [Fact]
    public void Budget_UsedUp_HealthReportsIt()
    {
        var ledger = new MemoryLedger();
        ledger.Record("cloud", 5, DateTimeOffset.UtcNow);
        var provider = new FakeChatProvider("cloud") { Budget = new TokenBudget(5, TimeSpan.FromHours(1)) };

        var health = new GetHealthHandler(new ProviderRegistry([provider]), new NoDisabled(), ledger).Handle(stopping: false);

        Assert.Equal(ProviderState.BudgetExhausted, health.Providers.Single().Status.State);
        Assert.Equal(OverallHealth.Unhealthy, health.Overall);
    }

    /// <summary>The caller gets the token counts of an OpenAI-style stream only when it asked for them. Other protocols always send them.</summary>
    /// <param name="fields">The template, separated by ','.</param>
    /// <param name="askUsage">Whether the caller sets stream_options.include_usage.</param>
    /// <param name="expected">Whether the caller must get the counts.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("messages,stream_options", false, false)]
    [InlineData("messages,stream_options", true, true)]
    [InlineData("messages,system", false, true)]
    public async Task Stream_ForwardUsage_FollowsCaller(string fields, bool askUsage, bool expected)
    {
        var provider = new FakeChatProvider("cloud") { RequestFields = fields.Split(','), Chunks = [new ChatChunk("Hi", FinishReason.Stop, new TokenUsage(1, 1))] };
        var body = new JsonObject { ["messages"] = new JsonArray() };
        if (askUsage)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        var result = await new StreamChatHandler(new ProviderRegistry([provider])).HandleAsync(new("cloud/m", body), TestContext.Current.CancellationToken);
        await using var answer = result.Value!;

        Assert.Equal(expected, answer.ForwardUsage);
    }

    /// <summary>Builds a command.</summary>
    /// <returns>The command.</returns>
    private static CompleteChatCommand Command() =>
        new("cloud/m", new JsonObject { ["messages"] = JsonNode.Parse("""[{"role":"user","content":"Hello there"}]""") });

    /// <summary>A ledger in memory, with one bucket for each record.</summary>
    private sealed class MemoryLedger : IUsageLedger
    {
        /// <summary>The records.</summary>
        private readonly List<(string Provider, long Tokens, DateTimeOffset Time)> records = [];

        /// <summary>Gives all tokens of a provider.</summary>
        /// <param name="provider">The provider.</param>
        /// <returns>The tokens.</returns>
        public long Total(string provider) => records.Where(record => record.Provider == provider).Sum(record => record.Tokens);

        /// <inheritdoc/>
        public long Used(string provider, TimeSpan period, DateTimeOffset now) =>
            records.Where(record => record.Provider == provider && record.Time > now - period).Sum(record => record.Tokens);

        /// <inheritdoc/>
        public DateTimeOffset AvailableAt(string provider, TokenBudget budget, DateTimeOffset now) =>
            records.Where(record => record.Provider == provider).Min(record => record.Time) + budget.Period;

        /// <inheritdoc/>
        public void Record(string provider, long tokens, DateTimeOffset now) => records.Add((provider, tokens, now));
    }

    /// <summary>No disabled providers.</summary>
    private sealed class NoDisabled : IDisabledProviders
    {
        /// <inheritdoc/>
        public IReadOnlyList<string> Names => [];
    }
}

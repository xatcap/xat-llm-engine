using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Diagnostics;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Tests;

/// <summary>Tests the metrics of chat requests.</summary>
public sealed class TelemetryTests : IDisposable
{
    /// <summary>Creates the meters, and keeps them for the collectors.</summary>
    private readonly TestMeterFactory meters = new();

    /// <summary>The telemetry under test.</summary>
    private readonly EngineTelemetry telemetry;

    /// <summary>Initializes a new instance of the <see cref="TelemetryTests"/> class.</summary>
    public TelemetryTests() => telemetry = new EngineTelemetry(meters);

    /// <inheritdoc/>
    public void Dispose()
    {
        telemetry.Dispose();
        meters.Dispose();
    }

    /// <summary>A complete request counts once, with its outcome, and its tokens count by direction.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_Success_CountsRequestAndTokens()
    {
        using var requests = Collect<long>("xat_engine.chat.requests");
        using var tokens = Collect<long>("xat_engine.tokens");
        var provider = new FakeChatProvider("cloud") { Models = ["model-a"], Usage = new TokenUsage(7, 3) };

        await new CompleteChatHandler(new ProviderRegistry([provider]), telemetry).HandleAsync(Command("cloud/model-a"), TestContext.Current.CancellationToken);

        var request = Assert.Single(requests.GetMeasurementSnapshot());
        Assert.Equal("ok", request.Tags["outcome"]);
        Assert.Equal("model-a", request.Tags["model"]);
        Assert.Equal(7, tokens.GetMeasurementSnapshot().Single(item => (string?)item.Tags["direction"] == "input").Value);
        Assert.Equal(3, tokens.GetMeasurementSnapshot().Single(item => (string?)item.Tags["direction"] == "output").Value);
    }

    /// <summary>A model that the configuration does not list gets the tag 'other', so the number of tag values stays small.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_UnlistedModel_UsesOtherTag()
    {
        using var requests = Collect<long>("xat_engine.chat.requests");

        await new CompleteChatHandler(new ProviderRegistry([new FakeChatProvider("cloud")]), telemetry).HandleAsync(Command("cloud/any-name"), TestContext.Current.CancellationToken);

        Assert.Equal("other", Assert.Single(requests.GetMeasurementSnapshot()).Tags["model"]);
    }

    /// <summary>A failed request counts with the kind of failure.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_Failure_CountsOutcome()
    {
        using var requests = Collect<long>("xat_engine.chat.requests");
        var provider = new FakeChatProvider("cloud") { Failure = new EngineError(ErrorKind.RateLimited, "Slow down.") };

        await new CompleteChatHandler(new ProviderRegistry([provider]), telemetry).HandleAsync(Command("cloud/m"), TestContext.Current.CancellationToken);

        Assert.Equal("rate_limited", Assert.Single(requests.GetMeasurementSnapshot()).Tags["outcome"]);
    }

    /// <summary>A stream counts when it ends, with the time to the first piece and the tokens.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_Read_CountsAtEnd()
    {
        using var requests = Collect<long>("xat_engine.chat.requests");
        using var firstChunk = Collect<double>("xat_engine.chat.time_to_first_chunk");
        var provider = new FakeChatProvider("cloud") { Chunks = [new ChatChunk("Hi", FinishReason.Stop, new TokenUsage(2, 1))] };
        var result = await new StreamChatHandler(new ProviderRegistry([provider]), telemetry).HandleAsync(Command("cloud/m"), TestContext.Current.CancellationToken);

        await using (var answer = result.Value!)
        {
            Assert.Empty(requests.GetMeasurementSnapshot());
            await foreach (var _ in answer.ReadAsync())
            {
            }
        }

        Assert.Single(firstChunk.GetMeasurementSnapshot());
        var request = Assert.Single(requests.GetMeasurementSnapshot());
        Assert.Equal("ok", request.Tags["outcome"]);
        Assert.Equal(true, request.Tags["stream"]);
    }

    /// <summary>A stream that the caller does not read to the end counts as canceled.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_NotRead_CountsAsCanceled()
    {
        using var requests = Collect<long>("xat_engine.chat.requests");
        var result = await new StreamChatHandler(new ProviderRegistry([new FakeChatProvider("cloud")]), telemetry).HandleAsync(Command("cloud/m"), TestContext.Current.CancellationToken);

        await result.Value!.DisposeAsync();

        Assert.Equal("canceled", Assert.Single(requests.GetMeasurementSnapshot()).Tags["outcome"]);
    }

    /// <summary>Builds a command.</summary>
    /// <param name="model">The model reference.</param>
    /// <returns>The command.</returns>
    private static CompleteChatCommand Command(string model) => new(model, new JsonObject { ["messages"] = new JsonArray() });

    /// <summary>Collects one instrument of the engine's meter.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="instrument">The instrument name.</param>
    /// <returns>The collector.</returns>
    private MetricCollector<T> Collect<T>(string instrument)
        where T : struct => new(meters.Created.Single(), instrument);

    /// <summary>Creates meters and keeps them, so that a test can collect their values.</summary>
    private sealed class TestMeterFactory : IMeterFactory
    {
        /// <summary>Gets the created meters.</summary>
        public List<Meter> Created { get; } = [];

        /// <inheritdoc/>
        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            Created.Add(meter);
            return meter;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            foreach (var meter in Created)
            {
                meter.Dispose();
            }
        }
    }
}

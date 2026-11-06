using System.Diagnostics;
using System.Diagnostics.Metrics;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Diagnostics;

/// <summary>The engine's metrics and traces, with the .NET built-in types only. Read them with <c>dotnet-counters</c> or <c>dotnet-trace</c>.</summary>
/// <remarks>
/// A model tag holds a configured model name or <c>other</c>. Callers can send any name, and a tag with many values uses
/// a lot of memory in a metrics tool.
/// </remarks>
public sealed class EngineTelemetry : IDisposable
{
    /// <summary>The name of the meter and of the activity source.</summary>
    public const string Name = "XatLlmEngine";

    /// <summary>The model tag for a model name that the configuration does not list.</summary>
    internal const string OtherModel = "other";

    /// <summary>The source of the engine's traces.</summary>
    internal static readonly ActivitySource Source = new(Name);

    /// <summary>The meter.</summary>
    private readonly Meter meter;

    /// <summary>Counts chat requests.</summary>
    private readonly Counter<long> requests;

    /// <summary>Measures the time of a whole chat request.</summary>
    private readonly Histogram<double> duration;

    /// <summary>Measures the time until the first piece of a stream.</summary>
    private readonly Histogram<double> firstChunk;

    /// <summary>Counts tokens.</summary>
    private readonly Counter<long> tokens;

    /// <summary>Initializes a new instance of the <see cref="EngineTelemetry"/> class.</summary>
    /// <param name="meterFactory">Creates the meter, so that the host and the tests can collect its values.</param>
    public EngineTelemetry(IMeterFactory meterFactory)
    {
        meter = meterFactory.Create(Name);
        requests = meter.CreateCounter<long>("xat_engine.chat.requests", "{request}", "Chat requests, by provider, model, stream and outcome.");
        duration = meter.CreateHistogram<double>("xat_engine.chat.duration", "s", "The time of a whole chat request.");
        firstChunk = meter.CreateHistogram<double>("xat_engine.chat.time_to_first_chunk", "s", "The time until the first piece of a stream.");
        tokens = meter.CreateCounter<long>("xat_engine.tokens", "{token}", "Tokens, by provider, model, direction and source: reported by the provider, or estimated by the engine.");
    }

    /// <summary>Gets telemetry that nothing collects, for code that runs without a host.</summary>
    internal static EngineTelemetry Unregistered { get; } = new(new StandaloneMeterFactory());

    /// <summary>Starts the measurement of one chat request.</summary>
    /// <param name="provider">The selected provider.</param>
    /// <param name="model">The model name inside the provider.</param>
    /// <param name="stream">Whether the request asks for a stream.</param>
    /// <returns>The measurement. Record its end once.</returns>
    internal ChatMeasurement Start(IChatProvider provider, string model, bool stream)
    {
        var modelTag = provider.Models.Contains(model, StringComparer.Ordinal) ? model : OtherModel;
        var activity = Source.StartActivity("chat", ActivityKind.Internal);
        activity?.SetTag("xat_engine.provider", provider.Name);
        activity?.SetTag("xat_engine.model", modelTag);
        activity?.SetTag("xat_engine.stream", stream);
        return new ChatMeasurement(this, provider.Name, modelTag, stream, activity);
    }

    /// <inheritdoc/>
    public void Dispose() => meter.Dispose();

    /// <summary>Gives the outcome tag of a failure. These values are stable, so they never come from enum names.</summary>
    /// <param name="kind">The failure, or <see langword="null"/> for a success.</param>
    /// <returns>The tag value.</returns>
    private static string Outcome(ErrorKind? kind) => kind is not { } failure ? "ok" : failure switch
    {
        ErrorKind.InvalidRequest => "invalid_request",
        ErrorKind.NotFound => "not_found",
        ErrorKind.RateLimited => "rate_limited",
        ErrorKind.Unavailable => "unavailable",
        ErrorKind.Upstream => "upstream",
        ErrorKind.Overloaded => "overloaded",
        ErrorKind.BudgetExceeded => "budget_exceeded",
    };

    /// <summary>The measurement of one chat request.</summary>
    internal sealed class ChatMeasurement
    {
        /// <summary>The telemetry.</summary>
        private readonly EngineTelemetry owner;

        /// <summary>The tags of every value.</summary>
        private readonly TagList tags;

        /// <summary>The trace of the request, if a listener asked for it.</summary>
        private readonly Activity? activity;

        /// <summary>The start time.</summary>
        private readonly long started = Stopwatch.GetTimestamp();

        /// <summary>1 after the measurement has finished.</summary>
        private int finished;

        /// <summary>Initializes a new instance of the <see cref="ChatMeasurement"/> class.</summary>
        /// <param name="owner">The telemetry.</param>
        /// <param name="provider">The provider tag.</param>
        /// <param name="model">The model tag.</param>
        /// <param name="stream">The stream tag.</param>
        /// <param name="activity">The trace, if any.</param>
        public ChatMeasurement(EngineTelemetry owner, string provider, string model, bool stream, Activity? activity)
        {
            this.owner = owner;
            this.activity = activity;
            tags = new TagList { { "provider", provider }, { "model", model }, { "stream", stream } };
        }

        /// <summary>Records that the first piece of a stream arrived.</summary>
        public void FirstChunk() => owner.firstChunk.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);

        /// <summary>Records the end of the request. Only the first call has an effect.</summary>
        /// <param name="failure">The failure, or <see langword="null"/> for a success.</param>
        /// <param name="usage">The token counts, if any.</param>
        /// <param name="estimated">Whether the engine estimated the counts, because the provider did not report them.</param>
        public void Finish(ErrorKind? failure, TokenUsage? usage, bool estimated = false) => Finish(Outcome(failure), usage, estimated);

        /// <summary>Records a request that the caller canceled, for example by a disconnect.</summary>
        /// <param name="usage">The token counts, if any.</param>
        /// <param name="estimated">Whether the engine estimated the counts.</param>
        public void Cancel(TokenUsage? usage = null, bool estimated = false) => Finish("canceled", usage, estimated);

        /// <summary>Records a request that failed with an unexpected exception.</summary>
        public void Crash() => Finish("error", usage: null, estimated: false);

        /// <summary>Records the end of the request. Only the first call has an effect.</summary>
        /// <param name="outcome">The outcome tag.</param>
        /// <param name="usage">The token counts, if any.</param>
        /// <param name="estimated">Whether the engine estimated the counts.</param>
        private void Finish(string outcome, TokenUsage? usage, bool estimated)
        {
            if (Interlocked.Exchange(ref finished, 1) == 1)
            {
                return;
            }

            var withOutcome = tags;
            withOutcome.Add("outcome", outcome);
            owner.requests.Add(1, withOutcome);
            owner.duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, withOutcome);
            if (usage is not null)
            {
                var source = estimated ? "estimated" : "reported";
                var input = tags;
                input.Add("direction", "input");
                input.Add("source", source);
                owner.tokens.Add(usage.InputTokens, input);
                var output = tags;
                output.Add("direction", "output");
                output.Add("source", source);
                owner.tokens.Add(usage.OutputTokens, output);
            }

            activity?.SetTag("xat_engine.outcome", outcome);
            activity?.SetStatus(outcome == "ok" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            activity?.Dispose();
        }
    }

    /// <summary>Creates meters when no host gives a factory.</summary>
    private sealed class StandaloneMeterFactory : IMeterFactory
    {
        /// <inheritdoc/>
        public Meter Create(MeterOptions options) => new(options);

        /// <inheritdoc/>
        public void Dispose()
        {
        }
    }
}

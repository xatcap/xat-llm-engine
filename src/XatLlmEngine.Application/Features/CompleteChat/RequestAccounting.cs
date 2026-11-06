using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Diagnostics;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Records the end of one chat request: its metrics, and its tokens for the budget.</summary>
/// <remarks>
/// When the provider does not report the tokens of an answer that reached the engine, the engine estimates them. Then a budget
/// also counts a stream that broke, and a provider that never reports tokens.
/// </remarks>
/// <param name="provider">The provider.</param>
/// <param name="body">The request body, for an estimate of the input tokens.</param>
/// <param name="budget">The budget rules.</param>
/// <param name="measurement">The metrics of the request.</param>
internal sealed class RequestAccounting(IChatProvider provider, JsonObject body, BudgetPolicy budget, EngineTelemetry.ChatMeasurement measurement)
{
    /// <summary>Gets the metrics of the request.</summary>
    public EngineTelemetry.ChatMeasurement Measurement => measurement;

    /// <summary>Records a request that failed before any answer reached the engine. Such a request counts no tokens.</summary>
    /// <param name="failure">The failure.</param>
    public void FailedBeforeAnswer(ErrorKind failure) => measurement.Finish(failure, usage: null);

    /// <summary>Records a request after an answer, or a part of one, reached the engine.</summary>
    /// <param name="failure">The failure, or <see langword="null"/> for a success.</param>
    /// <param name="reported">The tokens that the provider reported, if any.</param>
    /// <param name="outputBytes">The UTF-8 bytes of the answer text that arrived.</param>
    public void Answered(ErrorKind? failure, TokenUsage? reported, long outputBytes)
    {
        var (usage, estimated) = Count(reported, outputBytes);
        budget.Record(provider, usage);
        measurement.Finish(failure, usage, estimated);
    }

    /// <summary>Records a request that the caller canceled before an answer arrived. The provider can have billed the input, so the estimate of the input counts.</summary>
    public void CanceledBeforeAnswer()
    {
        var usage = new TokenUsage(UsageEstimate.Input(body), 0);
        budget.Record(provider, usage);
        measurement.Cancel(usage, estimated: true);
    }

    /// <summary>Records a request that the caller canceled after an answer started.</summary>
    /// <param name="reported">The tokens that the provider reported, if any.</param>
    /// <param name="outputBytes">The UTF-8 bytes of the answer text that arrived.</param>
    public void Canceled(TokenUsage? reported, long outputBytes)
    {
        var (usage, estimated) = Count(reported, outputBytes);
        budget.Record(provider, usage);
        measurement.Cancel(usage, estimated);
    }

    /// <summary>Gives the reported tokens, or an estimate.</summary>
    /// <param name="reported">The tokens that the provider reported, if any.</param>
    /// <param name="outputBytes">The UTF-8 bytes of the answer text.</param>
    /// <returns>The tokens, and whether they are an estimate.</returns>
    private (TokenUsage Usage, bool Estimated) Count(TokenUsage? reported, long outputBytes) =>
        reported is not null ? (reported, false) : (UsageEstimate.For(body, outputBytes), true);
}

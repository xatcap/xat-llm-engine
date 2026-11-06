using System.Text;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Diagnostics;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Produces a complete chat answer from the provider named by the caller.</summary>
/// <param name="providers">The enabled providers.</param>
/// <param name="telemetry">The metrics and traces, or <see langword="null"/> when nothing collects them.</param>
/// <param name="ledger">The token use over time, or <see langword="null"/> when no budget can apply.</param>
/// <param name="timeProvider">The clock, or <see langword="null"/> for the system clock.</param>
public sealed class CompleteChatHandler(
    ProviderRegistry providers,
    EngineTelemetry? telemetry = null,
    IUsageLedger? ledger = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>The budget rules.</summary>
    private readonly BudgetPolicy budget = new(ledger, timeProvider ?? TimeProvider.System);

    /// <summary>Validates the command and asks the selected provider for an answer.</summary>
    /// <param name="command">The caller's request.</param>
    /// <param name="cancellationToken">Cancels generation, for example when the caller disconnects.</param>
    /// <returns>The answer with its full model reference, or the expected failure.</returns>
    public async Task<Result<(ChatCompletion Completion, string Model)>> HandleAsync(
        CompleteChatCommand command,
        CancellationToken cancellationToken)
    {
        var routed = ChatRouter.Route(providers, command);
        if (!routed.IsSuccess)
        {
            return Result<(ChatCompletion, string)>.Fail(routed.Error);
        }

        var chat = routed.Value;
        var accounting = new RequestAccounting(
            chat.Provider,
            chat.Request.Body,
            budget,
            (telemetry ?? EngineTelemetry.Unregistered).Start(chat.Provider, chat.Request.Model, stream: false));
        if (budget.Check(chat.Provider) is { } budgetProblem)
        {
            accounting.FailedBeforeAnswer(budgetProblem.Kind);
            return Result<(ChatCompletion, string)>.Fail(budgetProblem);
        }

        Result<ChatCompletion> completion;
        try
        {
            completion = await chat.Provider.CompleteAsync(chat.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            accounting.CanceledBeforeAnswer();
            throw;
        }
        catch
        {
            accounting.Measurement.Crash();
            throw;
        }

        if (!completion.IsSuccess)
        {
            accounting.FailedBeforeAnswer(completion.Error.Kind);
            return Result<(ChatCompletion, string)>.Fail(completion.Error);
        }

        accounting.Answered(failure: null, completion.Value.Usage, Encoding.UTF8.GetByteCount(completion.Value.Content));
        return Result<(ChatCompletion, string)>.Ok((completion.Value, chat.Model.ToString()));
    }
}

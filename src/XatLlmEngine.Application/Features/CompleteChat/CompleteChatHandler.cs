using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Diagnostics;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Produces a complete chat answer from the provider named by the caller.</summary>
/// <param name="providers">The enabled providers.</param>
/// <param name="telemetry">The metrics and traces, or <see langword="null"/> when nothing collects them.</param>
public sealed class CompleteChatHandler(
    ProviderRegistry providers,
    EngineTelemetry? telemetry = null)
{
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
        var measurement = (telemetry ?? EngineTelemetry.Unregistered).Start(chat.Provider, chat.Request.Model, stream: false);

        Result<ChatCompletion> completion;
        try
        {
            completion = await chat.Provider.CompleteAsync(chat.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            measurement.Cancel();
            throw;
        }
        catch
        {
            measurement.Crash();
            throw;
        }

        if (!completion.IsSuccess)
        {
            measurement.Finish(completion.Error.Kind, usage: null);
            return Result<(ChatCompletion, string)>.Fail(completion.Error);
        }

        measurement.Finish(failure: null, completion.Value.Usage);
        return Result<(ChatCompletion, string)>.Ok((completion.Value, chat.Model.ToString()));
    }
}

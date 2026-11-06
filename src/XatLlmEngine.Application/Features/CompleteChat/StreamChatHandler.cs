using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Diagnostics;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Starts a streamed chat answer from the provider named by the caller.</summary>
/// <param name="providers">The enabled providers.</param>
/// <param name="telemetry">The metrics and traces, or <see langword="null"/> when nothing collects them.</param>
/// <param name="ledger">The token use over time, or <see langword="null"/> when no budget can apply.</param>
/// <param name="timeProvider">The clock, or <see langword="null"/> for the system clock.</param>
public sealed class StreamChatHandler(
    ProviderRegistry providers,
    EngineTelemetry? telemetry = null,
    IUsageLedger? ledger = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>The field of the OpenAI protocol that asks for token counts in a stream.</summary>
    private const string StreamOptionsField = "stream_options";

    /// <summary>The budget rules.</summary>
    private readonly BudgetPolicy budget = new(ledger, timeProvider ?? TimeProvider.System);

    /// <summary>Validates the command, starts the stream and reads its first piece.</summary>
    /// <remarks>
    /// A failure before the first piece must still become a normal HTTP error. Thus this method reads the first piece before it
    /// returns. Reading it also starts the provider's stream, so its connection returns to the pool when the caller disposes the result.
    /// </remarks>
    /// <param name="command">The caller's request.</param>
    /// <param name="cancellationToken">Cancels generation, for example when the caller disconnects.</param>
    /// <returns>The started stream, which the caller must dispose, or a failure that occurred before the first piece.</returns>
    public async Task<Result<StreamedAnswer>> HandleAsync(CompleteChatCommand command, CancellationToken cancellationToken)
    {
        var routed = ChatRouter.Route(providers, command);
        if (!routed.IsSuccess)
        {
            return Result<StreamedAnswer>.Fail(routed.Error);
        }

        var chat = routed.Value;
        var accounting = new RequestAccounting(
            chat.Provider,
            chat.Request.Body,
            budget,
            (telemetry ?? EngineTelemetry.Unregistered).Start(chat.Provider, chat.Request.Model, stream: true));
        if (budget.Check(chat.Provider) is { } budgetProblem)
        {
            accounting.FailedBeforeAnswer(budgetProblem.Kind);
            return Result<StreamedAnswer>.Fail(budgetProblem);
        }

        // The OpenAI protocol sends token counts only when the caller asks for them. The adapter asks for them always, for the
        // budget and the metrics, so the stream gives them to the caller only when the caller asked. Other protocols always send them.
        var forwardUsage = !chat.Provider.RequestFields.Contains(StreamOptionsField, StringComparer.Ordinal) || CallerAskedForUsage(command.Body);
        IAsyncEnumerator<ChatChunk>? source = null;
        var keep = false;
        try
        {
            var stream = await chat.Provider.StreamAsync(chat.Request, cancellationToken).ConfigureAwait(false);
            if (!stream.IsSuccess)
            {
                accounting.FailedBeforeAnswer(stream.Error.Kind);
                return Result<StreamedAnswer>.Fail(stream.Error);
            }

            source = stream.Value.GetAsyncEnumerator(cancellationToken);
            if (!await source.MoveNextAsync().ConfigureAwait(false))
            {
                var empty = new EngineError(ErrorKind.Upstream, "The provider ended the stream without an answer.");
                accounting.FailedBeforeAnswer(empty.Kind);
                return Result<StreamedAnswer>.Fail(empty);
            }

            accounting.Measurement.FirstChunk();
            keep = true;
            return Result<StreamedAnswer>.Ok(new StreamedAnswer(source, source.Current, chat.Model.ToString(), forwardUsage, accounting));
        }
        catch (ChatStreamException exception)
        {
            accounting.FailedBeforeAnswer(exception.Error.Kind);
            return Result<StreamedAnswer>.Fail(exception.Error);
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
        finally
        {
            if (!keep && source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Tells whether the caller set <c>stream_options.include_usage</c>.</summary>
    /// <param name="body">The caller's body.</param>
    /// <returns><see langword="true"/> when the caller asked for token counts.</returns>
    private static bool CallerAskedForUsage(JsonObject body) =>
        body[StreamOptionsField] is JsonObject streamOptions
        && streamOptions["include_usage"] is JsonValue value
        && value.TryGetValue<bool>(out var includeUsage)
        && includeUsage;
}

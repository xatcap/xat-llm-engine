using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.SystemPrompts;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Produces a complete chat answer from the provider named by the caller.</summary>
public sealed class CompleteChatHandler
{
    /// <summary>The shared validation and provider selection.</summary>
    private readonly ChatRouter router;

    /// <summary>Initializes a new instance of the <see cref="CompleteChatHandler"/> class.</summary>
    /// <param name="providers">Every provider enabled in this deployment.</param>
    /// <param name="systemPrompts">The stored system prompts.</param>
    public CompleteChatHandler(IEnumerable<IChatProvider> providers, SystemPromptStore systemPrompts)
    {
        router = new ChatRouter(providers, systemPrompts);
    }

    /// <summary>Validates the command and asks the selected provider for an answer.</summary>
    /// <param name="command">The caller's request.</param>
    /// <param name="cancellationToken">Cancels generation, for example when the caller disconnects.</param>
    /// <returns>The answer with its full model reference, or the expected failure.</returns>
    public async Task<Result<(ChatCompletion Completion, string Model)>> HandleAsync(
        CompleteChatCommand command,
        CancellationToken cancellationToken)
    {
        var routed = router.Route(command);
        if (!routed.IsSuccess)
        {
            return Result<(ChatCompletion, string)>.Fail(routed.Error);
        }

        var chat = routed.Value!;
        var completion = await chat.Provider.CompleteAsync(chat.Request, cancellationToken).ConfigureAwait(false);
        return completion.IsSuccess
            ? Result<(ChatCompletion, string)>.Ok((completion.Value!, chat.Model.ToString()))
            : Result<(ChatCompletion, string)>.Fail(completion.Error);
    }
}

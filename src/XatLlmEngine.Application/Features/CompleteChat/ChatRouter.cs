using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Validates chat commands and selects the provider named by the model prefix.</summary>
/// <param name="providers">Every provider enabled in this deployment.</param>
internal sealed class ChatRouter(IEnumerable<IChatProvider> providers)
{
    /// <summary>The enabled providers by name. Names ignore case, because configuration keys do too.</summary>
    private readonly Dictionary<string, IChatProvider> providersByName =
        providers.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Applies the shared rules for a chat request.</summary>
    /// <param name="command">The caller's request.</param>
    /// <returns>The routed request, or an invalid-request or not-found failure.</returns>
    public Result<RoutedChat> Route(CompleteChatCommand command)
    {
        var model = ModelReference.Parse(command.Model);
        if (!model.IsSuccess)
        {
            return Result<RoutedChat>.Fail(model.Error);
        }

        var invalid = Validate(command);
        if (invalid is not null)
        {
            return Result<RoutedChat>.Fail(new EngineError(ErrorKind.InvalidRequest, invalid));
        }

        if (!providersByName.TryGetValue(model.Value!.Provider, out var provider))
        {
            return Result<RoutedChat>.Fail(new EngineError(
                ErrorKind.NotFound,
                $"The provider '{model.Value.Provider}' is not available. Check its name, and that its API key is configured."));
        }

        var request = new ChatRequest(
            model.Value.Model,
            command.Messages,
            command.Temperature,
            command.TopP,
            command.MaxTokens,
            command.Stop ?? []);
        return Result<RoutedChat>.Ok(new RoutedChat(provider, request, model.Value));
    }

    /// <summary>Checks the value ranges that hold for every provider.</summary>
    /// <param name="command">The caller's request.</param>
    /// <returns>A message describing the first problem, or <see langword="null"/> when the request is valid.</returns>
    private static string? Validate(CompleteChatCommand command) => command switch
    {
        { Messages.Count: 0 } => "At least one message is required.",
        { Temperature: < 0 } => "The temperature must not be negative.",
        { TopP: < 0 or > 1 } => "The top_p value must be between 0 and 1.",
        { MaxTokens: <= 0 } => "The maximum token count must be positive.",
        _ => null,
    };
}

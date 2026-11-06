using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Selects the provider named by the model prefix, checks the request against that provider's rules, and renames portable fields.</summary>
internal static class ChatRouter
{
    /// <summary>Applies the rules for a chat request.</summary>
    /// <param name="providers">The enabled providers.</param>
    /// <param name="command">The caller's request.</param>
    /// <returns>The routed request with its own copy of the body, or an invalid-request or not-found failure.</returns>
    public static Result<RoutedChat> Route(ProviderRegistry providers, CompleteChatCommand command)
    {
        var model = ModelReference.Parse(command.Model);
        if (!model.IsSuccess)
        {
            return Result<RoutedChat>.Fail(model.Error);
        }

        if (!providers.TryGet(model.Value.Provider, out var provider))
        {
            return Result<RoutedChat>.Fail(new EngineError(
                ErrorKind.NotFound,
                $"The provider '{model.Value.Provider}' is not available. Check its name, and check that the configuration has its API key."));
        }

        var fields = PortableFields.Resolve(provider, command.Body);
        if (!fields.IsSuccess)
        {
            return Result<RoutedChat>.Fail(fields.Error);
        }

        if (MessageRules.FindToolContent(command.Body) is { } toolContent)
        {
            return Result<RoutedChat>.Fail(toolContent);
        }

        // This is the only copy. The provider owns it and can change it, so the caller's body never changes.
        var body = command.Body.DeepClone().AsObject();
        fields.Value.ApplyTo(body);
        if (TokenLimitPolicy.Apply(provider, body, fields.Value.SentName) is { } limitProblem)
        {
            return Result<RoutedChat>.Fail(limitProblem);
        }

        return Result<RoutedChat>.Ok(new RoutedChat(provider, new ChatRequest(model.Value.Model, body), model.Value, fields.Value));
    }
}

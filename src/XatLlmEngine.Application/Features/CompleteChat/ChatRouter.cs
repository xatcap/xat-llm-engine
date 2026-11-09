using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Selects the provider named by the model prefix and checks the request against that provider's rules.</summary>
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

        if (FindUnsupportedField(provider, command.Body) is { } unsupported)
        {
            return Result<RoutedChat>.Fail(unsupported);
        }

        // This is the only copy. The provider owns it and can change it, so the caller's body never changes.
        var body = command.Body.DeepClone().AsObject();
        if (TokenLimitPolicy.Apply(provider, body) is { } limitProblem)
        {
            return Result<RoutedChat>.Fail(limitProblem);
        }

        return Result<RoutedChat>.Ok(new RoutedChat(provider, new ChatRequest(model.Value.Model, body), model.Value));
    }

    /// <summary>Checks that every top-level field in the request is in the provider's template.</summary>
    /// <param name="provider">The selected provider.</param>
    /// <param name="body">The caller's body.</param>
    /// <returns>The error for the first unsupported field, or <see langword="null"/> when all fields are supported.</returns>
    private static EngineError? FindUnsupportedField(IChatProvider provider, JsonObject body)
    {
        foreach (var (name, _) in body)
        {
            if (name is not ("model" or "stream") && !provider.RequestFields.Contains(name, StringComparer.Ordinal))
            {
                return new EngineError(
                    ErrorKind.InvalidRequest,
                    $"The provider '{provider.Name}' does not support the field '{name}'. Supported fields: model, stream, {string.Join(", ", provider.RequestFields)}.");
            }
        }

        return null;
    }
}

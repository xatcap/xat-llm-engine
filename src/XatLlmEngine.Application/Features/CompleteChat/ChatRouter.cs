using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Application.Features.SystemPrompts;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Selects the provider named by the model prefix, and checks the request against that provider's template.</summary>
/// <param name="providers">Every provider enabled in this deployment.</param>
/// <param name="systemPrompts">The stored system prompts.</param>
internal sealed class ChatRouter(IEnumerable<IChatProvider> providers, SystemPromptStore systemPrompts)
{
    /// <summary>The enabled providers by name. Names ignore case, because configuration keys do too.</summary>
    private readonly Dictionary<string, IChatProvider> providersByName =
        providers.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Applies the rules for a chat request.</summary>
    /// <param name="command">The caller's request.</param>
    /// <returns>The routed request, or an invalid-request or not-found failure.</returns>
    public Result<RoutedChat> Route(CompleteChatCommand command)
    {
        var model = ModelReference.Parse(command.Model);
        if (!model.IsSuccess)
        {
            return Result<RoutedChat>.Fail(model.Error);
        }

        if (!providersByName.TryGetValue(model.Value!.Provider, out var provider))
        {
            return Result<RoutedChat>.Fail(new EngineError(
                ErrorKind.NotFound,
                $"The provider '{model.Value.Provider}' is not available. Check its name, and check that the configuration has its API key."));
        }

        if (HasOwnSystemPrompt(command.Body))
        {
            return Result<RoutedChat>.Fail(new EngineError(
                ErrorKind.InvalidRequest,
                $"A chat request cannot set a system prompt. Use PUT /v1/system-prompts/{provider.Name} instead."));
        }

        var unsupported = command.Body.Select(field => field.Key).FirstOrDefault(name => !IsAccepted(provider, name));
        if (unsupported is not null)
        {
            return Result<RoutedChat>.Fail(new EngineError(
                ErrorKind.InvalidRequest,
                $"The provider '{provider.Name}' does not support the field '{unsupported}'. Supported fields: model, stream, {string.Join(", ", provider.RequestFields)}."));
        }

        return Result<RoutedChat>.Ok(new RoutedChat(provider, new ChatRequest(model.Value.Model, command.Body, systemPrompts.Get(provider.Name)), model.Value));
    }

    /// <summary>Tells whether a request sets its own system prompt, in either the Anthropic format or the OpenAI format.</summary>
    /// <param name="body">The caller's JSON.</param>
    /// <returns><see langword="true"/> when the request has a system prompt.</returns>
    private static bool HasOwnSystemPrompt(JsonObject body) =>
        body.ContainsKey("system")
        || (body["messages"] is JsonArray messages && messages.Any(message =>
            message is JsonObject fields
            && fields["role"] is JsonValue role
            && role.TryGetValue<string>(out var name)
            && (name.Equals("system", StringComparison.OrdinalIgnoreCase) || name.Equals("developer", StringComparison.OrdinalIgnoreCase))));

    /// <summary>Tells whether a provider accepts a field. Every provider accepts <c>model</c> and <c>stream</c>, because the engine needs them to route the request.</summary>
    /// <param name="provider">The selected provider.</param>
    /// <param name="name">The field's name.</param>
    /// <returns><see langword="true"/> when the engine can forward the field.</returns>
    private static bool IsAccepted(IChatProvider provider, string name) =>
        name is "model" or "stream" || provider.RequestFields.Contains(name, StringComparer.Ordinal);
}

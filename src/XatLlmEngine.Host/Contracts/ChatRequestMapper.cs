using System.Text.Json;
using XatLlmEngine.Application.Features.CompleteChat;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Host.Contracts;

/// <summary>Converts the OpenAI request format into a use-case command.</summary>
internal static class ChatRequestMapper
{
    /// <summary>Converts a request, and rejects features the engine does not support, so that callers never get a silently different result.</summary>
    /// <param name="request">The request body.</param>
    /// <returns>The command, or an invalid-request failure.</returns>
    public static Result<CompleteChatCommand> ToCommand(ChatCompletionRequest request)
    {
        if (request.Tools is { ValueKind: JsonValueKind.Array } tools && tools.GetArrayLength() > 0)
        {
            return Invalid("Tool calling is not supported yet.");
        }

        if (request.N is not (null or 1))
        {
            return Invalid("Only one answer per request is supported, so 'n' must be 1.");
        }

        if (request.ResponseFormat?.Type is not (null or "text"))
        {
            return Invalid("Only the 'text' response format is supported.");
        }

        var messages = new List<ChatMessage>(request.Messages?.Count ?? 0);
        foreach (var message in request.Messages ?? [])
        {
            if (message is null)
            {
                return Invalid("A message must not be null.");
            }

            var role = message.Role switch
            {
                "system" or "developer" => ChatRole.System,
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                _ => (ChatRole?)null,
            };
            if (role is null)
            {
                return Invalid($"The message role '{message.Role}' is not supported. Use system, developer, user or assistant.");
            }

            var content = ReadContent(message.Content);
            if (!content.IsSuccess)
            {
                return Result<CompleteChatCommand>.Fail(content.Error);
            }

            messages.Add(new ChatMessage(role.Value, content.Value!));
        }

        var stop = ReadStop(request.Stop);
        if (!stop.IsSuccess)
        {
            return Result<CompleteChatCommand>.Fail(stop.Error);
        }

        return Result<CompleteChatCommand>.Ok(new CompleteChatCommand(
            request.Model,
            messages,
            request.Temperature,
            request.TopP,
            request.MaxCompletionTokens ?? request.MaxTokens,
            stop.Value));
    }

    /// <summary>Builds an invalid-request failure for a command.</summary>
    /// <param name="message">The description of the problem.</param>
    /// <returns>The failure.</returns>
    private static Result<CompleteChatCommand> Invalid(string message) =>
        Result<CompleteChatCommand>.Fail(new EngineError(ErrorKind.InvalidRequest, message));

    /// <summary>Reads message content, which is a string or an array of parts. Only text parts are supported.</summary>
    /// <param name="content">The JSON value.</param>
    /// <returns>The joined text, or an invalid-request failure.</returns>
    private static Result<string> ReadContent(JsonElement? content)
    {
        if (content is { ValueKind: JsonValueKind.String } text)
        {
            return Result<string>.Ok(text.GetString()!);
        }

        if (content is not { ValueKind: JsonValueKind.Array } parts)
        {
            return Result<string>.Fail(new EngineError(ErrorKind.InvalidRequest, "Message content must be a string or an array of text parts."));
        }

        var joined = new System.Text.StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || !part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "text"
                || !part.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
            {
                return Result<string>.Fail(new EngineError(ErrorKind.InvalidRequest, "Only text content parts are supported."));
            }

            joined.Append(value.GetString());
        }

        return Result<string>.Ok(joined.ToString());
    }

    /// <summary>Reads stop sequences, which are one string or an array of strings.</summary>
    /// <param name="stop">The JSON value.</param>
    /// <returns>The sequences, or an invalid-request failure.</returns>
    private static Result<IReadOnlyList<string>> ReadStop(JsonElement? stop)
    {
        switch (stop)
        {
            case null or { ValueKind: JsonValueKind.Null }:
                return Result<IReadOnlyList<string>>.Ok([]);
            case { ValueKind: JsonValueKind.String } single:
                return Result<IReadOnlyList<string>>.Ok([single.GetString()!]);
            case { ValueKind: JsonValueKind.Array } many when many.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String):
                return Result<IReadOnlyList<string>>.Ok([.. many.EnumerateArray().Select(item => item.GetString()!)]);
            default:
                return Result<IReadOnlyList<string>>.Fail(new EngineError(ErrorKind.InvalidRequest, "'stop' must be a string or an array of strings."));
        }
    }
}

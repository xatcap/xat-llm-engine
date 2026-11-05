using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>The system prompt rules that the local model and every cloud provider share.</summary>
internal static class SystemPromptRules
{
    /// <summary>The highest value that the configuration can set for a limit, in characters. A larger limit would let one prompt use too much memory and be sent with every request.</summary>
    public const int LengthCeiling = 1_000_000;

    /// <summary>The default limit, in characters. This is about 2000 tokens of English text, which leaves most of the local model's default context for the conversation.</summary>
    public const int DefaultLength = 8_000;

    /// <summary>Checks a configured prompt against its limit.</summary>
    /// <param name="prompt">The configured prompt.</param>
    /// <param name="limit">The configured limit.</param>
    /// <returns>The problems.</returns>
    public static IEnumerable<ValidationResult> ValidateSettings(string? prompt, int limit)
    {
        if (prompt?.Length > limit)
        {
            yield return new ValidationResult($"SystemPrompt has {prompt.Length} characters, but MaxSystemPromptLength is {limit}.");
        }
    }

    /// <summary>Checks a chat request before an adapter adds the stored prompt. A request cannot set its own prompt, and it must have a <c>messages</c> list, so that no request can avoid the stored prompt.</summary>
    /// <param name="body">The caller's JSON.</param>
    /// <returns>The problem, or <see langword="null"/> when the request is correct.</returns>
    public static EngineError? FindProblem(JsonObject body)
    {
        if (body.ContainsKey("system"))
        {
            return OwnPrompt();
        }

        if (body["messages"] is not JsonArray messages)
        {
            return new EngineError(ErrorKind.InvalidRequest, "The 'messages' field must be a list.");
        }

        return messages.Any(IsSystemMessage) ? OwnPrompt() : null;
    }

    /// <summary>Builds the failure for a request that sets its own prompt.</summary>
    /// <returns>The failure.</returns>
    private static EngineError OwnPrompt() =>
        new(ErrorKind.InvalidRequest, "A chat request cannot set a system prompt. Change the provider's stored system prompt instead.");

    /// <summary>Tells whether a message sets a system prompt. The check ignores case, so that a different spelling cannot avoid it.</summary>
    /// <param name="message">The message.</param>
    /// <returns><see langword="true"/> when the message has the role <c>system</c> or <c>developer</c>.</returns>
    private static bool IsSystemMessage(JsonNode? message) =>
        message is JsonObject fields
        && fields["role"] is JsonValue role
        && role.TryGetValue<string>(out var name)
        && (name.Equals("system", StringComparison.OrdinalIgnoreCase) || name.Equals("developer", StringComparison.OrdinalIgnoreCase));
}

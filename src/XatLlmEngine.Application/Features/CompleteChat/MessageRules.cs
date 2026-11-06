using System.Text.Json.Nodes;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Refuses tool content inside <c>messages</c>, for every provider.</summary>
/// <remarks>
/// The engine does not support tools, because the unified response carries one text answer. The template check refuses the
/// top-level <c>tools</c> field. Without this check, tool content inside a message would reach the provider. A cloud provider
/// then returns its own error, and the local model can give a strange answer. See ADR 0002.
/// </remarks>
internal static class MessageRules
{
    /// <summary>The message roles that only tool use needs.</summary>
    private static readonly string[] ToolRoles = ["tool", "function"];

    /// <summary>The message fields that only tool use needs.</summary>
    private static readonly string[] ToolFields = ["tool_calls", "function_call"];

    /// <summary>The content block types that only tool use needs. Anthropic's server tools have their own result types, which all end with <c>_tool_result</c>.</summary>
    private static readonly string[] ToolBlocks = ["tool_use", "tool_result", "server_tool_use", "mcp_tool_use", "mcp_tool_result"];

    /// <summary>Finds the first tool content in the messages.</summary>
    /// <param name="body">The request body.</param>
    /// <returns>The error for the first tool content, or <see langword="null"/> when the messages have none.</returns>
    public static EngineError? FindToolContent(JsonObject body)
    {
        if (body["messages"] is not JsonArray messages)
        {
            // The provider checks the shape of the messages.
            return null;
        }

        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is JsonObject message && Describe(message) is { } problem)
            {
                return new EngineError(ErrorKind.InvalidRequest, $"Message {index} {problem}. The engine does not support tools.");
            }
        }

        return null;
    }

    /// <summary>Describes the tool content of one message.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The description, or <see langword="null"/> when the message has no tool content.</returns>
    private static string? Describe(JsonObject message)
    {
        if (Text(message["role"]) is { } role && ToolRoles.Contains(role, StringComparer.Ordinal))
        {
            return $"has the role '{role}'";
        }

        if (ToolFields.FirstOrDefault(field => message[field] is not null) is { } toolField)
        {
            return $"has the field '{toolField}'";
        }

        var block = message["content"] is JsonArray blocks
            ? blocks.OfType<JsonObject>().Select(item => Text(item["type"])).FirstOrDefault(IsToolBlock)
            : null;
        return block is null ? null : $"has a content block of the type '{block}'";
    }

    /// <summary>Tells whether a content block type belongs to tool use.</summary>
    /// <param name="type">The block type.</param>
    /// <returns><see langword="true"/> for a tool block.</returns>
    private static bool IsToolBlock(string? type) =>
        type is not null && (ToolBlocks.Contains(type, StringComparer.Ordinal) || type.EndsWith("_tool_result", StringComparison.Ordinal));

    /// <summary>Reads a JSON string.</summary>
    /// <param name="node">The value.</param>
    /// <returns>The text, or <see langword="null"/> when the value is not a string.</returns>
    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

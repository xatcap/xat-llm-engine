using System.Text.Json.Nodes;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>A caller's request for a chat answer, not yet routed.</summary>
/// <param name="Model">The target written as <c>provider/model</c>.</param>
/// <param name="Body">The caller's JSON, in the format of the selected provider.</param>
public sealed record CompleteChatCommand(string? Model, JsonObject Body);

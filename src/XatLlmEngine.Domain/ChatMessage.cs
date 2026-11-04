namespace XatLlmEngine.Domain;

/// <summary>One text message in a conversation.</summary>
/// <param name="Role">Who wrote the message.</param>
/// <param name="Content">The message text.</param>
public sealed record ChatMessage(ChatRole Role, string Content);

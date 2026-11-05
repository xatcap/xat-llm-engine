namespace XatLlmEngine.Infrastructure.Providers.Anthropic;

/// <summary>A complete Messages API response, also carried inside the <c>message_start</c> event.</summary>
/// <param name="Content">The content blocks of the answer.</param>
/// <param name="StopReason">Why generation stopped.</param>
/// <param name="Usage">Token counts.</param>
internal sealed record AnthropicResponse(IReadOnlyList<AnthropicContentBlock>? Content, string? StopReason, AnthropicUsage? Usage);

/// <summary>One content block of an answer.</summary>
/// <param name="Type">The block type. The engine reads only <c>text</c> blocks.</param>
/// <param name="Text">The block's text.</param>
internal sealed record AnthropicContentBlock(string? Type, string? Text);

/// <summary>Token counts. A <c>message_delta</c> event may send only the output count.</summary>
/// <param name="InputTokens">Tokens in the prompt.</param>
/// <param name="OutputTokens">Tokens generated.</param>
internal sealed record AnthropicUsage(int? InputTokens, int? OutputTokens);

/// <summary>A streaming event. Each event type fills only some of these values.</summary>
/// <param name="Type">The event type.</param>
/// <param name="Message">The message that <c>message_start</c> opens.</param>
/// <param name="Delta">The change in a <c>content_block_delta</c> or <c>message_delta</c> event.</param>
/// <param name="Usage">Token counts in a <c>message_delta</c> event.</param>
internal sealed record AnthropicEvent(string? Type, AnthropicResponse? Message, AnthropicDelta? Delta, AnthropicUsage? Usage);

/// <summary>The change carried by an event.</summary>
/// <param name="Type">The delta type. Only <c>text_delta</c> carries answer text.</param>
/// <param name="Text">New answer text.</param>
/// <param name="StopReason">Why generation stopped.</param>
internal sealed record AnthropicDelta(string? Type, string? Text, string? StopReason);

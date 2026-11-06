using System.Text;
using System.Text.Json.Nodes;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>Estimates the tokens of a request when the provider does not report them.</summary>
/// <remarks>
/// A token is about 4 bytes of English text, and fewer for other languages. The estimate uses 3 bytes, so that it is more
/// likely too high than too low. A budget then stops a little early, not late.
/// </remarks>
internal static class UsageEstimate
{
    /// <summary>The bytes of text for one token.</summary>
    private const int BytesPerToken = 3;

    /// <summary>Estimates the input tokens from the messages and the system prompt.</summary>
    /// <param name="body">The request body.</param>
    /// <returns>The estimate.</returns>
    public static int Input(JsonObject body) =>
        Tokens(Encoding.UTF8.GetByteCount(body["messages"]?.ToJsonString() ?? string.Empty) + Encoding.UTF8.GetByteCount(body["system"]?.ToJsonString() ?? string.Empty));

    /// <summary>Estimates the tokens of a request.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="outputBytes">The UTF-8 bytes of the answer text.</param>
    /// <returns>The estimate.</returns>
    public static TokenUsage For(JsonObject body, long outputBytes) => new(Input(body), Tokens(outputBytes));

    /// <summary>Converts bytes to tokens, rounded up.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The tokens.</returns>
    private static int Tokens(long bytes) => (int)Math.Min(int.MaxValue, (bytes + BytesPerToken - 1) / BytesPerToken);
}

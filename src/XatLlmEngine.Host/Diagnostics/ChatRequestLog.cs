namespace XatLlmEngine.Host.Diagnostics;

/// <summary>The log of finished chat requests. Each request gives one line, so that an operator can find slow or failing providers.</summary>
/// <remarks>The line never holds the prompt or the answer.</remarks>
internal static partial class ChatRequestLog
{
    /// <summary>The log category.</summary>
    public const string Category = "XatLlmEngine.Host.ChatRequests";

    /// <summary>Logs a finished chat request.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="model">The model, as the caller wrote it.</param>
    /// <param name="stream">Whether the answer was a stream.</param>
    /// <param name="status">The HTTP status.</param>
    /// <param name="outcome">How the request ended: <c>ok</c>, the error type, or <c>error_event</c> for a stream that failed after it started.</param>
    /// <param name="milliseconds">The time of the request.</param>
    /// <param name="inputTokens">The input tokens, if the provider reported them.</param>
    /// <param name="outputTokens">The output tokens, if the provider reported them.</param>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Chat request for {Model} (stream: {Stream}) ended with status {Status} ({Outcome}) after {Milliseconds} ms. Tokens: {InputTokens} in, {OutputTokens} out.")]
    public static partial void Finished(ILogger logger, string? model, bool stream, int status, string outcome, long milliseconds, int? inputTokens, int? outputTokens);
}

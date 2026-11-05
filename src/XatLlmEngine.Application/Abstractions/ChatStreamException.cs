using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Abstractions;

/// <summary>Reports that a stream failed after it started, when a <see cref="Result{T}"/> can no longer be returned.</summary>
public sealed class ChatStreamException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ChatStreamException"/> class.</summary>
    /// <param name="error">The failure, with a message that is safe to show to the caller.</param>
    /// <param name="innerException">The cause, if there is one.</param>
    public ChatStreamException(EngineError error, Exception? innerException = null)
        : base(error.Message, innerException)
    {
        Error = error;
    }

    /// <summary>Gets the failure.</summary>
    public EngineError Error { get; }
}

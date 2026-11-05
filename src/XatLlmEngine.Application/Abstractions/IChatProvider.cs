using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Abstractions;

/// <summary>A source of chat answers, such as llama.cpp or a cloud API.</summary>
/// <remarks>
/// This is a port (Dependency Inversion Principle): use cases depend on it, and Infrastructure implements it.
/// A new provider therefore needs no change to any use case. Implementations must be thread-safe.
/// </remarks>
public interface IChatProvider
{
    /// <summary>Gets the name callers use as the prefix of a model, for example <c>anthropic</c>.</summary>
    string Name { get; }

    /// <summary>Gets the model names to advertise. A provider may also accept names not in this list.</summary>
    IReadOnlyList<string> Models { get; }

    /// <summary>Gets the request fields this provider accepts, besides <c>model</c> and <c>stream</c>. This list is the provider's request template.</summary>
    IReadOnlyList<string> RequestFields { get; }

    /// <summary>Generates a complete answer.</summary>
    /// <param name="request">The validated request.</param>
    /// <param name="cancellationToken">Cancels generation, for example when the caller disconnects.</param>
    /// <returns>The answer, or the expected failure.</returns>
    Task<Result<ChatCompletion>> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);

    /// <summary>Starts generating a streamed answer.</summary>
    /// <param name="request">The validated request.</param>
    /// <param name="cancellationToken">Cancels generation, for example when the caller disconnects.</param>
    /// <returns>
    /// The stream, or a failure if generation could not start. The result must be enumerated once so that the
    /// connection is released. Failures during enumeration throw <see cref="ChatStreamException"/>.
    /// </returns>
    Task<Result<IAsyncEnumerable<ChatChunk>>> StreamAsync(ChatRequest request, CancellationToken cancellationToken);
}

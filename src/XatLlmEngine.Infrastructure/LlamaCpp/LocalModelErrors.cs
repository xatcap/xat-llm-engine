using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Gives the error for a local request in each stage of the local model.</summary>
internal static class LocalModelErrors
{
    /// <summary>The error for a request that finds the queue full.</summary>
    public static readonly EngineError QueueFull = new(ErrorKind.Overloaded, "The local model has too many requests at this time. Try again shortly.");

    /// <summary>Gives the error for a new request in a stage.</summary>
    /// <param name="snapshot">The state of the local model.</param>
    /// <returns>The error, or <see langword="null"/> when the model can take new requests.</returns>
    public static EngineError? ForNewRequest(LlamaServerSnapshot snapshot) => snapshot.State switch
    {
        LlamaServerState.Ready => null,
        LlamaServerState.Starting => new(ErrorKind.Unavailable, "The local model is loading. Try again shortly."),
        LlamaServerState.Unresponsive => new(ErrorKind.Unavailable, "llama-server does not respond to health checks at this time. Try again shortly."),
        LlamaServerState.Restarting => new(ErrorKind.Unavailable, "llama-server stopped unexpectedly, and the engine starts it again. Try again shortly."),
        LlamaServerState.Stopped => new(ErrorKind.Unavailable, "The engine is stopping."),

        // A retry does not help in these stages, so they are not Unavailable errors with a wait time.
        LlamaServerState.Missing => new(ErrorKind.Upstream, "The local model is not installed. Download it from the GitHub release into the models folder, then restart the engine."),
        LlamaServerState.Failed => new(ErrorKind.Upstream, $"The local model failed. {snapshot.Failure?.PublicMessage}"),
    };
}

using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Gives the error for a local request in each stage of a local model.</summary>
internal static class LocalModelErrors
{
    /// <summary>Gives the error for a request that finds the queue full.</summary>
    /// <param name="role">The identity of the model.</param>
    /// <returns>The error.</returns>
    public static EngineError QueueFull(LlamaModelRole role) =>
        new(ErrorKind.Overloaded, $"The {role.Label} model has too many requests at this time. Try again shortly.");

    /// <summary>Gives the error for a new request in a stage.</summary>
    /// <param name="snapshot">The state of the model.</param>
    /// <param name="role">The identity of the model.</param>
    /// <returns>The error, or <see langword="null"/> when the model can take new requests.</returns>
    public static EngineError? ForNewRequest(LlamaServerSnapshot snapshot, LlamaModelRole role) => snapshot.State switch
    {
        LlamaServerState.Ready => null,
        LlamaServerState.Starting => new(ErrorKind.Unavailable, $"The {role.Label} model is loading. Try again shortly."),
        LlamaServerState.Unresponsive => new(ErrorKind.Unavailable, $"llama-server of the {role.Label} model does not respond to health checks at this time. Try again shortly."),
        LlamaServerState.Restarting => new(ErrorKind.Unavailable, $"llama-server of the {role.Label} model stopped unexpectedly, and the engine starts it again. Try again shortly."),
        LlamaServerState.Stopped => new(ErrorKind.Unavailable, "The engine is stopping."),

        // A retry does not help in these stages, so they are not Unavailable errors with a wait time.
        LlamaServerState.Missing => new(ErrorKind.Upstream, role.IsMain
            ? "The local model is not installed. Download it from the GitHub release into the models folder, then restart the engine."
            : $"The {role.Label} model file is missing. The release package includes it in the models folder. Install the package again, then restart the engine."),
        LlamaServerState.Failed => new(ErrorKind.Upstream, $"The {role.Label} model failed. {snapshot.Failure?.PublicMessage}"),
    };
}

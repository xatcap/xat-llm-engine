namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>The stages of the local model's life.</summary>
internal enum LlamaServerState
{
    /// <summary>The server is starting or the model is loading.</summary>
    Starting,

    /// <summary>The model is loaded and can serve requests.</summary>
    Ready,

    /// <summary>The server stopped unexpectedly and will be started again after a delay.</summary>
    Restarting,

    /// <summary>The model file or the server program is missing, so the local provider stays off.</summary>
    Missing,

    /// <summary>The engine is shutting down.</summary>
    Stopped,
}

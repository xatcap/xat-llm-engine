namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>The stages of the local model's life.</summary>
internal enum LlamaServerState
{
    /// <summary>The server is starting or the model is loading.</summary>
    Starting,

    /// <summary>The model is loaded and can serve requests.</summary>
    Ready,

    /// <summary>The server stopped unexpectedly after it was ready. The engine will start it again after a delay.</summary>
    Restarting,

    /// <summary>The model file or the server program is missing, so the local provider stays off.</summary>
    Missing,

    /// <summary>The engine is stopping.</summary>
    Stopped,

    /// <summary>llama-server could not start, or it stopped too often. The engine does not try again until the engine restarts.</summary>
    Failed,
}

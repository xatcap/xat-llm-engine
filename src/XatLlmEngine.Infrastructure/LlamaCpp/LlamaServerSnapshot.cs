namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>The state of the local model at one moment. The supervisor replaces the whole snapshot, so that the state, the address and the failure always agree.</summary>
/// <param name="State">The stage.</param>
/// <param name="Endpoint">The address of the running server. It is set only in <see cref="LlamaServerState.Ready"/> and <see cref="LlamaServerState.Unresponsive"/>.</param>
/// <param name="Failure">Why the local model failed. It is set only in <see cref="LlamaServerState.Failed"/>.</param>
internal sealed record LlamaServerSnapshot(LlamaServerState State, LlamaServerEndpoint? Endpoint = null, LlamaFailure? Failure = null);

/// <summary>Why the local model failed.</summary>
/// <param name="PublicMessage">The message for callers. It holds no file paths, exception text or server output.</param>
/// <param name="Detail">The full cause, for the engine log only.</param>
internal sealed record LlamaFailure(string PublicMessage, string Detail);

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>How to reach the running llama-server.</summary>
/// <param name="BaseUrl">The address including the version path.</param>
/// <param name="ApiKey">The random key created for this run.</param>
internal sealed record LlamaServerEndpoint(Uri BaseUrl, string ApiKey);

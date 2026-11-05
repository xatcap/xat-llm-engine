namespace XatLlmEngine.Host.Contracts;

/// <summary>The body of <c>GET /health</c>.</summary>
/// <param name="Status">Either <c>healthy</c> or <c>degraded</c>.</param>
/// <param name="Local">The local model's stage: for example <c>ready</c>, <c>starting</c>, <c>missing</c> or <c>disabled</c>.</param>
internal sealed record HealthResponse(string Status, string Local);

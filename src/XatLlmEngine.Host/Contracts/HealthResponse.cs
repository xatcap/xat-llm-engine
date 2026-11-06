namespace XatLlmEngine.Host.Contracts;

/// <summary>The body of <c>GET /health</c> and <c>GET /health/ready</c>. See ADR 0010.</summary>
/// <param name="Status">The overall health: <c>healthy</c>, <c>degraded</c> or <c>unhealthy</c>.</param>
/// <param name="Version">The engine version.</param>
/// <param name="Local">The local model's state, or <c>disabled</c>. This field is for older callers. Read <paramref name="Providers"/> instead.</param>
/// <param name="Message">What the local model's state means. This field is for older callers.</param>
/// <param name="Providers">Each configured provider, enabled or not.</param>
internal sealed record HealthResponse(string Status, string Version, string Local, string Message, IReadOnlyList<ProviderHealthResponse> Providers);

/// <summary>The health of one provider.</summary>
/// <param name="Name">The provider's name, as callers write it before the <c>/</c> in a model name.</param>
/// <param name="State">The provider's state, for example <c>ready</c>, <c>busy</c>, <c>starting</c> or <c>disabled</c>.</param>
/// <param name="Message">What the state means for callers.</param>
/// <param name="Active">The requests that have a slot. Only for a provider that queues requests in the engine.</param>
/// <param name="Queued">The requests that wait for a slot.</param>
/// <param name="Capacity">The number of slots.</param>
/// <param name="QueueLimit">The number of requests that can wait.</param>
/// <param name="Budget">The token budget use. Only for a provider that has a token budget.</param>
internal sealed record ProviderHealthResponse(string Name, string State, string Message, int? Active = null, int? Queued = null, int? Capacity = null, int? QueueLimit = null, BudgetResponse? Budget = null);

/// <summary>The token budget use of one provider. See ADR 0011.</summary>
/// <param name="Used">The tokens, input and output together, that the provider used in the rolling period before now.</param>
/// <param name="Limit">The token budget of the period.</param>
/// <param name="PeriodHours">The length of the rolling period, in hours.</param>
/// <param name="AvailableAt">Only when the state is <c>budget_exhausted</c>: the time when requests can continue.</param>
internal sealed record BudgetResponse(long Used, long Limit, double PeriodHours, DateTimeOffset? AvailableAt = null);

/// <summary>The body of <c>GET /health/live</c>.</summary>
/// <param name="Status">Always <c>alive</c>.</param>
internal sealed record LivenessResponse(string Status);

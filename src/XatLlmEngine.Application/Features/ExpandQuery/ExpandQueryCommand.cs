namespace XatLlmEngine.Application.Features.ExpandQuery;

/// <summary>A caller's request to expand one search query.</summary>
/// <param name="Query">The search query to expand.</param>
/// <param name="Count">How many other queries the caller wants.</param>
public sealed record ExpandQueryCommand(string Query, int Count);

/// <summary>The other search queries for one search query.</summary>
/// <param name="Queries">The new queries. The list never holds the original query or a repeated query.</param>
/// <param name="Model">The full model reference, for example <c>query-expansion/qwen3-0.6b</c>.</param>
public sealed record ExpandedQuery(IReadOnlyList<string> Queries, string Model);

namespace XatLlmEngine.Host.Contracts;

/// <summary>The request body of <c>POST /v1/query-expansion</c>. The route reads the JSON itself, so that it can reject a wrong type or an unknown field with a clear message. This type describes the body in the API document.</summary>
/// <param name="Query">The search query to expand.</param>
/// <param name="Count">How many other queries to write. The default is 3. The largest value is 10.</param>
internal sealed record QueryExpansionRequest(string Query, int? Count);

/// <summary>The answer of <c>POST /v1/query-expansion</c>.</summary>
/// <param name="Queries">The new queries. The list never holds the original query or a repeated query.</param>
/// <param name="Model">The model that wrote the queries, written as <c>provider/model</c>.</param>
internal sealed record QueryExpansionResponse(IReadOnlyList<string> Queries, string Model);

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace XatLlmEngine.Host.Endpoints;

/// <summary>Completes the generated API document: its title, the engine version and a working chat example.</summary>
internal sealed class ApiDocumentTransformer : IOpenApiDocumentTransformer
{
    /// <summary>Describes the chat request body. Swagger UI puts the example into "Try it out", so the example must be a request that the API accepts.</summary>
    /// <param name="document">The generated document.</param>
    private static void DescribeChatRequest(OpenApiDocument document)
    {
        var body = document.Paths["/v1/chat/completions"].Operations?[HttpMethod.Post].RequestBody as OpenApiRequestBody;
        if (body is null)
        {
            return;
        }

        body.Description = "The chat request, in the format of the provider named in 'model'. A field that the provider does not accept returns a 400 error.";
        if (body.Content?["application/json"] is OpenApiMediaType media)
        {
            media.Example = JsonNode.Parse("""{"model":"local/ministral-3-3b","messages":[{"role":"user","content":"Hello"}]}""");
        }
    }

    /// <summary>Describes the query-expansion request body with an example that the API accepts.</summary>
    /// <param name="document">The generated document.</param>
    private static void DescribeQueryExpansionRequest(OpenApiDocument document)
    {
        var body = document.Paths["/v1/query-expansion"].Operations?[HttpMethod.Post].RequestBody as OpenApiRequestBody;
        if (body is null)
        {
            return;
        }

        body.Description = "The search query, and how many other queries to write. 'count' is optional: the default is 3, and the largest value is 10.";
        if (body.Content?["application/json"] is OpenApiMediaType media)
        {
            media.Example = JsonNode.Parse("""{"query":"how to reset a router","count":3}""");
        }
    }

    /// <inheritdoc/>
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "xat-llm-engine";
        document.Info.Version = EngineVersion.Value;
        DescribeChatRequest(document);
        DescribeQueryExpansionRequest(document);
        return Task.CompletedTask;
    }
}

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

        body.Description = "The chat request, in the format of the provider named in 'model'. Providers that use the OpenAI format also accept the portable fields. A field that the provider does not accept returns a 400 error.";
        if (body.Content?["application/json"] is OpenApiMediaType media)
        {
            media.Example = JsonNode.Parse("""{"model":"local/ministral-3-3b","messages":[{"role":"user","content":"Hello"}]}""");
        }
    }

    /// <inheritdoc/>
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "xat-llm-engine";
        document.Info.Version = EngineVersion.Value;
        DescribeChatRequest(document);
        return Task.CompletedTask;
    }
}

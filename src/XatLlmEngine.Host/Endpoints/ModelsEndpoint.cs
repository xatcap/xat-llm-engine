using XatLlmEngine.Application.Features.ListModels;
using XatLlmEngine.Host.Contracts;

namespace XatLlmEngine.Host.Endpoints;

/// <summary>Implements <c>GET /v1/models</c>.</summary>
internal static class ModelsEndpoint
{
    /// <summary>Lists the configured models as provider/model.</summary>
    /// <param name="handler">The use case.</param>
    /// <returns>The HTTP result.</returns>
    public static IResult Handle(ListModelsHandler handler)
    {
        var models = handler.Handle()
            .Select(id => new ModelResponse(id, "model", id[..id.IndexOf('/', StringComparison.Ordinal)]))
            .ToList();
        return Results.Json(new ModelListResponse("list", models), HostJson.Default.ModelListResponse);
    }
}

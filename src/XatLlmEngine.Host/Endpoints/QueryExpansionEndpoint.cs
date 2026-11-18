using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using XatLlmEngine.Application.Features.ExpandQuery;
using XatLlmEngine.Domain;
using XatLlmEngine.Host.Contracts;

namespace XatLlmEngine.Host.Endpoints;

/// <summary>Implements <c>POST /v1/query-expansion</c>.</summary>
internal static class QueryExpansionEndpoint
{
    /// <summary>The name of the field that holds the search query.</summary>
    private const string QueryField = "query";

    /// <summary>The name of the field that holds the number of queries to write.</summary>
    private const string CountField = "count";

    /// <summary>Writes other search queries for one search query.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="handler">The use case.</param>
    /// <param name="apiOptions">The API settings, for the body size limit.</param>
    /// <returns>The HTTP result.</returns>
    public static async Task<IResult> HandleAsync(HttpContext context, ExpandQueryHandler handler, IOptions<ApiOptions> apiOptions)
    {
        var cancellationToken = context.RequestAborted;
        try
        {
            var (body, failure) = await RequestBody.ReadObjectAsync(context, apiOptions.Value.MaxRequestBodyBytes, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return failure!;
            }

            var command = Parse(body);
            if (!command.IsSuccess)
            {
                return ErrorMapping.ToResult(context, command.Error);
            }

            var result = await handler.HandleAsync(command.Value, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? Results.Json(new QueryExpansionResponse(result.Value.Queries, result.Value.Model), HostJson.Default.QueryExpansionResponse)
                : ErrorMapping.ToResult(context, result.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller disconnected, so nobody is left to answer.
            return Results.Empty;
        }
    }

    /// <summary>Reads the command from the body. The route is strict: an unknown field or a value of the wrong type is an error, because a mistake in a field name would otherwise have no effect.</summary>
    /// <param name="body">The request body.</param>
    /// <returns>The command, or the error for the caller.</returns>
    private static Result<ExpandQueryCommand> Parse(JsonObject body)
    {
        foreach (var field in body)
        {
            if (field.Key is not (QueryField or CountField))
            {
                return Invalid($"The field '{field.Key}' is not allowed. Use '{QueryField}' and '{CountField}'.");
            }
        }

        if (body[QueryField] is not JsonValue queryValue || !queryValue.TryGetValue<string>(out var query))
        {
            return Invalid($"The '{QueryField}' field is required, and it must be a string.");
        }

        var count = ExpandQueryHandler.DefaultCount;
        if (body[CountField] is { } countNode && !(countNode is JsonValue countValue && countValue.TryGetValue(out count)))
        {
            return Invalid($"The '{CountField}' field must be a whole number.");
        }

        return Result<ExpandQueryCommand>.Ok(new ExpandQueryCommand(query, count));
    }

    /// <summary>Builds a failed result for a request that the caller must change.</summary>
    /// <param name="message">The message for the caller.</param>
    /// <returns>The failed result.</returns>
    private static Result<ExpandQueryCommand> Invalid(string message) =>
        Result<ExpandQueryCommand>.Fail(new EngineError(ErrorKind.InvalidRequest, message));
}

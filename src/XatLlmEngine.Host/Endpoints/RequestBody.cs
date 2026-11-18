using System.Text.Json;
using System.Text.Json.Nodes;
using XatLlmEngine.Domain;
using XatLlmEngine.Host.Contracts;

namespace XatLlmEngine.Host.Endpoints;

/// <summary>Reads and checks the JSON body of a request. Every route that takes a JSON object uses these rules.</summary>
internal static class RequestBody
{
    /// <summary>Reads a request body as a JSON object.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="limit">The largest body to accept, in bytes.</param>
    /// <param name="cancellationToken">Stops reading when the caller disconnects.</param>
    /// <returns>The object, or the error result to return to the caller. Exactly one of the two is set.</returns>
    public static async Task<(JsonObject? Body, IResult? Failure)> ReadObjectAsync(HttpContext context, long limit, CancellationToken cancellationToken)
    {
        // The engine requires JSON content because browsers cannot send it across sites without a CORS approval, which the engine never gives.
        if (!context.Request.HasJsonContentType())
        {
            return (null, ErrorMapping.UnsupportedMediaType());
        }

        var content = await ReadBytesAsync(context.Request, limit, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return (null, ErrorMapping.RequestTooLarge(limit));
        }

        JsonObject? body;
        try
        {
            body = JsonNode.Parse(content.AsSpan()) as JsonObject;

            // The JSON parser checks JSON only at first use. Without this check, a problem causes a later 500 error, possibly after the provider call.
            // ReadAll finds a repeated field name. ToJsonString finds a string that is not valid UTF-16, for example a lone "\ud83d".
            ReadAll(body);
            _ = body?.ToJsonString();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return (null, ErrorMapping.ToResult(context, new EngineError(ErrorKind.InvalidRequest, "The request body is not valid JSON.")));
        }

        return body is null
            ? (null, ErrorMapping.ToResult(context, new EngineError(ErrorKind.InvalidRequest, "The request body must be a JSON object.")))
            : (body, null);
    }

    /// <summary>Reads the request body, up to a limit. The body is read in full, so that the JSON parser can check all of it.</summary>
    /// <param name="request">The request.</param>
    /// <param name="limit">The largest body to accept, in bytes.</param>
    /// <param name="cancellationToken">Stops reading when the caller disconnects.</param>
    /// <returns>The body, or <see langword="null"/> when it is larger than the limit.</returns>
    private static async Task<byte[]?> ReadBytesAsync(HttpRequest request, long limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var block = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(block, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(block, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Opens every nested object and array, which fails on a repeated field name.</summary>
    /// <param name="node">The JSON value.</param>
    private static void ReadAll(JsonNode? node)
    {
        if (node is JsonObject fields)
        {
            foreach (var field in fields)
            {
                ReadAll(field.Value);
            }
        }
        else if (node is JsonArray items)
        {
            foreach (var item in items)
            {
                ReadAll(item);
            }
        }
    }
}

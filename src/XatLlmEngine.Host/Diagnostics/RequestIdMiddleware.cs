using System.Diagnostics;

namespace XatLlmEngine.Host.Diagnostics;

/// <summary>Gives each request an ID. The ID goes back in the <c>X-Request-Id</c> header, and every log line of the request holds it.</summary>
/// <remarks>The ID is the trace ID of the request, so it also matches a trace in <c>dotnet-trace</c>. A caller can send a W3C <c>traceparent</c> header to choose it.</remarks>
/// <param name="next">The rest of the pipeline.</param>
/// <param name="logger">The log, which holds the scope.</param>
internal sealed class RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
{
    /// <summary>The response header that holds the ID.</summary>
    public const string HeaderName = "X-Request-Id";

    /// <summary>The key under which the item holds the ID.</summary>
    public const string ItemKey = "XatLlmEngine.RequestId";

    /// <summary>Handles one request.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the rest of the pipeline has finished.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var id = Activity.Current?.TraceId.ToHexString() ?? context.TraceIdentifier;
        context.Items[ItemKey] = id;

        // The header goes on when the response starts, so the error handler cannot clear it.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = id }))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the ID of a request.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The ID, or the ASP.NET trace identifier when this middleware did not run.</returns>
    public static string IdOf(HttpContext context) => context.Items[ItemKey] as string ?? context.TraceIdentifier;
}

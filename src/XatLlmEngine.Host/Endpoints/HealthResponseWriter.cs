using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using XatLlmEngine.Host.Contracts;

namespace XatLlmEngine.Host.Endpoints;

/// <summary>Writes the body of <c>GET /health</c>.</summary>
internal static class HealthResponseWriter
{
    /// <summary>Writes the overall status and the local model's stage. It reveals no addresses, keys or paths, because this route needs no key.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="report">The health report.</param>
    /// <returns>A task that completes when the body has been written.</returns>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var local = report.Entries.TryGetValue("local", out var entry) ? entry.Description ?? "unknown" : "disabled";
        var body = new HealthResponse(report.Status.ToString().ToLowerInvariant(), local);
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, HostJson.Default.HealthResponse), context.RequestAborted);
    }
}

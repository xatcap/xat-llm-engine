using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using XatLlmEngine.Host.Contracts;

namespace XatLlmEngine.Host.Endpoints;

/// <summary>Writes the body of <c>GET /health</c>.</summary>
internal static class HealthResponseWriter
{
    /// <summary>Writes the overall status, the local model's stage, and what that stage means. It reveals no addresses, keys, paths or provider names, because this route needs no key.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="report">The health report.</param>
    /// <returns>A task that completes when the body has been written.</returns>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var local = report.Entries.TryGetValue("local", out var entry) ? entry.Description ?? "unknown" : "disabled";
        var body = new HealthResponse(report.Status.ToString().ToLowerInvariant(), local, Describe(local));
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, HostJson.Default.HealthResponse), context.RequestAborted);
    }

    /// <summary>Explains a stage of the local model.</summary>
    /// <param name="local">The stage's name.</param>
    /// <returns>The explanation.</returns>
    private static string Describe(string local) => local switch
    {
        "ready" => "The local model is ready.",
        "disabled" => "The local model is turned off in settings.",
        "starting" => "The local model is loading. Local requests return 503 until it is ready. Cloud providers are not affected.",
        "restarting" => "llama-server stopped or failed to load, and the engine is restarting it. Local requests return 503 until then. Cloud providers are not affected.",
        "missing" => "The model or server file is missing. Local requests return 503 until it is installed and the engine restarts. Cloud providers are not affected.",
        "stopped" => "The engine is shutting down.",
        _ => "The local model's stage is unknown.",
    };
}

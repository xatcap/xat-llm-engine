using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using XatLlmEngine.Host.Contracts;
using XatLlmEngine.Infrastructure.LlamaCpp;

namespace XatLlmEngine.Host.Endpoints;

/// <summary>Writes the body of <c>GET /health</c>.</summary>
internal static class HealthResponseWriter
{
    /// <summary>Writes the overall status, the local model's stage, and what that stage means. This route needs no key, so it shows no addresses, keys, paths or provider names.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="report">The health report.</param>
    /// <returns>A task that completes after the engine writes the body.</returns>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var local = report.Entries.TryGetValue(LocalModelHealth.EntryName, out var entry) ? entry.Description ?? "unknown" : "disabled";
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
        "starting" => "The local model is loading. Local requests return 503 until it is ready. Cloud providers continue to work.",
        "restarting" => "llama-server stopped or failed to load, and the engine is restarting it. Local requests return 503 until then. Cloud providers continue to work.",
        "missing" => "The model or server file is missing. Local requests return 503 until you install the file and restart the engine. Cloud providers continue to work.",
        "stopped" => "The engine is stopping.",
        _ => "The local model's stage is unknown.",
    };
}

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using XatLlmEngine.Host.Endpoints;
using XatLlmEngine.Infrastructure.LlamaCpp;

namespace XatLlmEngine.Host.Tests;

/// <summary>Tests that the health route explains every stage that the local model can report.</summary>
public class HealthResponseWriterTests
{
    /// <summary>The message that the writer uses for a stage it does not know.</summary>
    private const string UnknownMessage = "The local model's stage is unknown.";

    /// <summary>Gets every stage of the local model, written as the health check writes it.</summary>
    /// <remarks>The stage type is internal to Infrastructure, so the test finds it by name. A new or renamed stage then fails this test, not the health route.</remarks>
    public static TheoryData<string> Stages
    {
        get
        {
            var type = typeof(LocalModelHealth).Assembly.GetType("XatLlmEngine.Infrastructure.LlamaCpp.LlamaServerState", throwOnError: true)!;
            return [.. Enum.GetNames(type).Select(name => name.ToLowerInvariant())];
        }
    }

    /// <summary>Each stage gets its own explanation.</summary>
    /// <param name="stage">The stage, in lowercase.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [MemberData(nameof(Stages))]
    public async Task Write_EveryStage_HasExplanation(string stage)
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            [LocalModelHealth.EntryName] = new(HealthStatus.Degraded, stage, TimeSpan.Zero, null, null),
        };
        var context = new DefaultHttpContext();
        using var output = new MemoryStream();
        context.Response.Body = output;

        await HealthResponseWriter.WriteAsync(context, new HealthReport(entries, TimeSpan.Zero));

        using var body = JsonDocument.Parse(output.ToArray());
        Assert.Equal(stage, body.RootElement.GetProperty("local").GetString());
        Assert.NotEqual(UnknownMessage, body.RootElement.GetProperty("message").GetString());
    }
}

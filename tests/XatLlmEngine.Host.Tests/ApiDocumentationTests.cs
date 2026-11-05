using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace XatLlmEngine.Host.Tests;

/// <summary>Tests that the API documentation exists while debugging, and only then.</summary>
public class ApiDocumentationTests
{
    /// <summary>The engine serves Swagger UI and its document in the Development environment.</summary>
    /// <param name="route">The route to request.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("/swagger/index.html")]
    [InlineData("/openapi/v1.json")]
    public async Task Development_ServesDocumentation(string route)
    {
        await using var engine = EngineIn("Development");
        using var client = engine.CreateClient();

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Release packages run as Production, and must not expose the documentation.</summary>
    /// <param name="route">The route to request.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger")]
    [InlineData("/openapi/v1.json")]
    public async Task Production_DoesNotServeDocumentation(string route)
    {
        await using var engine = EngineIn("Production");
        using var client = engine.CreateClient();

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The document describes the real routes and the real names on the wire.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Document_DescribesRoutesWithWireNames()
    {
        await using var engine = EngineIn("Development");
        using var client = engine.CreateClient();

        var text = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(text);
        var paths = document.RootElement.GetProperty("paths");
        var chat = paths.GetProperty("/v1/chat/completions").GetProperty("post");
        Assert.True(chat.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));
        Assert.True(chat.GetProperty("responses").TryGetProperty("503", out _));
        Assert.True(chat.TryGetProperty("security", out _));
        Assert.True(paths.GetProperty("/v1/models").TryGetProperty("get", out _));

        Assert.True(document.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _));
        Assert.Equal("xat-llm-engine", document.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    /// <summary>The document says that the request format depends on the provider, because each provider has its own template.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Document_MatchesRequestRules()
    {
        await using var engine = EngineIn("Development");
        using var client = engine.CreateClient();

        var text = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(text);
        var body = document.RootElement.GetProperty("paths").GetProperty("/v1/chat/completions").GetProperty("post").GetProperty("requestBody");
        Assert.Contains("the provider named in 'model'", body.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    /// <summary>The example that Swagger UI fills in must be a request the API accepts.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Document_ExampleIsAcceptedByApi()
    {
        await using var engine = EngineIn("Development");
        using var client = engine.CreateClient();
        var text = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(text);
        var example = document.RootElement.GetProperty("paths").GetProperty("/v1/chat/completions").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("example").GetRawText();

        using var content = new StringContent(example, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/v1/chat/completions", UriKind.Relative), content, TestContext.Current.CancellationToken);

        // The local model is absent in this test, so 503 shows that the request passed validation and reached the provider.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>Hosts the engine in one environment.</summary>
    /// <param name="environment">The environment name.</param>
    /// <returns>The hosted engine.</returns>
    private static WebApplicationFactory<Program> EngineIn(string environment) =>
        new EngineFactory().WithWebHostBuilder(builder => builder.UseEnvironment(environment));
}

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
        Assert.True(paths.GetProperty("/v1/models").TryGetProperty("get", out _));

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

        // The local model is absent in this test, so 502 shows that the request passed validation and reached the provider.
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    /// <summary>
    /// The committed document <c>docs/openapi.json</c> is the same as the document of the code. Then each change of the contract
    /// shows in review, and the release attaches a correct file. To update the file, run the tests with <c>UPDATE_OPENAPI=1</c>.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Document_MatchesCommittedSnapshot()
    {
        await using var engine = EngineIn("Development");
        using var client = engine.CreateClient();
        var text = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var actual = Normalize(text);
        var path = Path.Combine(RepositoryRoot(), "docs", "openapi.json");

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(path, actual, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(path), "docs/openapi.json is missing. Run the tests with UPDATE_OPENAPI=1 to make it.");
        var expected = Normalize(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.True(expected == actual, "docs/openapi.json is not the same as the API of the code. Run the tests with UPDATE_OPENAPI=1, and review the change.");
    }

    /// <summary>Writes a document in one fixed form, so that only real changes count.</summary>
    /// <param name="text">The document.</param>
    /// <returns>The document, indented, with a final new line.</returns>
    private static string Normalize(string text)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();

        // The server address depends on the test host, not on the API. The release script writes the release version into the
        // file that it attaches to the release, so the committed file does not change for each version.
        node.Remove("servers");
        node["info"]!["version"] = "unreleased";
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return node.ToJsonString(options).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>Finds the folder of the repository, which holds the solution file.</summary>
    /// <returns>The folder.</returns>
    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "XatLlmEngine.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("The tests cannot find the repository folder.");
    }

    /// <summary>Hosts the engine in one environment.</summary>
    /// <param name="environment">The environment name.</param>
    /// <returns>The hosted engine.</returns>
    private static WebApplicationFactory<Program> EngineIn(string environment) =>
        new EngineFactory().WithWebHostBuilder(builder => builder.UseEnvironment(environment));
}

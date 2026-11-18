using System.Net;
using System.Text;
using System.Text.Json;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Host.Tests;

/// <summary>Tests the route <c>POST /v1/query-expansion</c> through the real API with a fake model.</summary>
public class QueryExpansionApiTests
{
    /// <summary>A valid request returns the new queries and the model name.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Expand_ValidRequest_ReturnsQueries()
    {
        await using var engine = new EngineFactory(withQueryExpansion: true);
        engine.QueryExpansion.Answer = "1. fix a router\n- router reset guide\n\"restart my router\"\nfactory reset router";

        using var response = await PostAsync(engine, """{"query":"how to reset a router","count":2}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(["fix a router", "router reset guide"], body.RootElement.GetProperty("queries").EnumerateArray().Select(entry => entry.GetString()).ToArray());
        Assert.Equal("query-expansion/model-a", body.RootElement.GetProperty("model").GetString());
    }

    /// <summary>The route gives the model its own prompt, so the caller never writes one.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Expand_ValidRequest_SendsPromptWithQuery()
    {
        await using var engine = new EngineFactory(withQueryExpansion: true);
        engine.QueryExpansion.Answer = "a\nb\nc";

        using var response = await PostAsync(engine, """{"query":"how to reset a router"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var messages = engine.QueryExpansion.LastRequest!.Body["messages"]!.AsArray();
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("how to reset a router", messages[1]!["content"]!.GetValue<string>());
        Assert.Contains("3 different", messages[0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>A request the caller must change gives 400.</summary>
    /// <param name="body">The request body.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":"   "}""")]
    [InlineData("""{"query":5}""")]
    [InlineData("""{"query":"x","count":0}""")]
    [InlineData("""{"query":"x","count":11}""")]
    [InlineData("""{"query":"x","count":1.5}""")]
    [InlineData("""{"query":"x","count":"3"}""")]
    [InlineData("""{"query":"x","other":1}""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    public async Task Expand_InvalidBody_Returns400(string body)
    {
        await using var engine = new EngineFactory(withQueryExpansion: true);

        using var response = await PostAsync(engine, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = await ReadJsonAsync(response);
        Assert.Equal("invalid_request_error", error.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    /// <summary>A body that is not JSON gives 415.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Expand_WrongContentType_Returns415()
    {
        await using var engine = new EngineFactory(withQueryExpansion: true);
        using var client = engine.CreateClient();
        using var content = new StringContent("""{"query":"x"}""", Encoding.UTF8, "text/plain");

        using var response = await client.PostAsync(new Uri("/v1/query-expansion", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    /// <summary>A body larger than the limit gives 413.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Expand_BodyTooLarge_Returns413()
    {
        await using var engine = new EngineFactory(new Dictionary<string, string?> { ["Api:MaxRequestBodyBytes"] = "1024" }, withQueryExpansion: true);

        using var response = await PostAsync(engine, "{\"query\":\"" + new string('x', 2000) + "\"}");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>A model that is off gives 503, and the message tells how to turn it on.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Expand_ModelOff_Returns503()
    {
        await using var engine = new EngineFactory();

        using var response = await PostAsync(engine, """{"query":"x"}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Contains("QueryExpansionModel:Enabled", body.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>An answer with no usable query gives 502.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Expand_EmptyAnswer_Returns502()
    {
        await using var engine = new EngineFactory(withQueryExpansion: true);
        engine.QueryExpansion.Answer = "  \n";

        using var response = await PostAsync(engine, """{"query":"x"}""");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    /// <summary>A failure of the model reaches the caller with its status.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Expand_ModelFails_MapsStatus()
    {
        await using var engine = new EngineFactory(withQueryExpansion: true);
        engine.QueryExpansion.Failure = new EngineError(ErrorKind.Overloaded, "Busy.");

        using var response = await PostAsync(engine, """{"query":"x"}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    /// <summary>The query-expansion model is in the list of models.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Models_WithQueryExpansion_ListsModel()
    {
        await using var engine = new EngineFactory(withQueryExpansion: true);
        using var client = engine.CreateClient();

        using var response = await client.GetAsync(new Uri("/v1/models", UriKind.Relative), TestContext.Current.CancellationToken);

        using var body = await ReadJsonAsync(response);
        Assert.Contains(body.RootElement.GetProperty("data").EnumerateArray(), entry => entry.GetProperty("id").GetString() == "query-expansion/model-a");
    }

    /// <summary>Sends a JSON body to the route.</summary>
    /// <param name="engine">The engine.</param>
    /// <param name="body">The request body.</param>
    /// <returns>The response.</returns>
    private static async Task<HttpResponseMessage> PostAsync(EngineFactory engine, string body)
    {
        using var client = engine.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri("/v1/query-expansion", UriKind.Relative), content, TestContext.Current.CancellationToken);
    }

    /// <summary>Reads a JSON response.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The parsed document.</returns>
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);
}

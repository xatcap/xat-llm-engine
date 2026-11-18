using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.Providers;
using XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests how the transport handles broken connections and size limits.</summary>
public class ProviderTransportTests
{
    /// <summary>A short wait, so that the tests run fast.</summary>
    private static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(200);

    /// <summary>A documented non-streaming response.</summary>
    private const string CompletionBody = """{"choices":[{"message":{"content":"Hi"},"finish_reason":"stop"}]}""";

    /// <summary>A broken connection while the engine reads a complete answer is a temporary failure (503), not an engine fault (500).</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ConnectionResetInBody_FailsAsUnavailable()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, """{"choices":[""") { ThrowWhileReading = new IOException("reset") };

        var result = await Client(http).CompleteAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error?.Kind);
    }

    /// <summary>A broken connection while the engine reads an error body gives the general message for the status.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ConnectionResetInErrorBody_KeepsStatus()
    {
        var http = new StubHttpHandler(HttpStatusCode.BadRequest, """{"error":""") { ThrowWhileReading = new IOException("reset") };

        var result = await Client(http).CompleteAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error?.Kind);
        Assert.Equal("The provider 'test' rejected the request.", result.Error?.Message);
    }

    /// <summary>A broken connection while the engine reads the error body of a stream also keeps the status.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_ConnectionResetInErrorBody_KeepsStatus()
    {
        var http = new StubHttpHandler(HttpStatusCode.ServiceUnavailable, """{"error":""") { ThrowWhileReading = new IOException("reset") };

        var result = await Client(http).StreamAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error?.Kind);
    }

    /// <summary>The engine sends a failed request once. It does not retry.</summary>
    /// <param name="status">The server's answer.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Complete_FailedResponse_SendsOnce(HttpStatusCode status)
    {
        var http = new StubHttpHandler(status, """{"error":{"message":"busy"}}""");

        var result = await Client(http).CompleteAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, http.RequestCount);
    }

    /// <summary>A connection failure is also not retried.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ConnectionFailure_SendsOnce()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, string.Empty) { Throw = new HttpRequestException("refused") };

        var result = await Client(http).CompleteAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error?.Kind);
        Assert.Equal(1, http.RequestCount);
    }

    /// <summary>The caller's own cancellation stays a cancellation. It is not an error from the provider.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_CallerCancels_Throws()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var http = new StubHttpHandler(HttpStatusCode.OK, CompletionBody) { HangBeforeHeaders = true };
        cancel.CancelAfter(ShortLimit);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(http).CompleteAsync(Address, "key", TestRequests.Minimal, cancel.Token));
    }

    /// <summary>An error body larger than the limit gives the general message, so that a provider cannot fill the engine's memory.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_LargeErrorBody_UsesGeneralMessage()
    {
        var message = new string('x', ProviderTransport.MaxErrorBodyBytes);
        var http = new StubHttpHandler(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"" + message + "\"}}");

        var result = await Client(http).CompleteAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal("The provider 'test' rejected the request.", result.Error?.Message);
    }

    /// <summary>An answer larger than the limit is refused.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_LargeAnswer_Fails()
    {
        var content = new string('x', (int)ProviderTransport.MaxResponseBytes);
        var http = new StubHttpHandler(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"" + content + "\"},\"finish_reason\":\"stop\"}]}");

        var result = await Client(http).CompleteAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Upstream, result.Error?.Kind);
    }

    /// <summary>An authentication failure of llama-server does not tell the user to correct a key, because the engine makes that key.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_LocalUnauthorized_GivesLocalAdvice()
    {
        var http = new StubHttpHandler(HttpStatusCode.Unauthorized, "{}");

        var result = await Client(http).CompleteAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Upstream, result.Error?.Kind);
        Assert.DoesNotContain("API key", result.Error?.Message, StringComparison.Ordinal);
        Assert.Contains("Restart the engine", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A redirect is an error. The engine does not follow it, so the key of the engine cannot go to another host.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_Redirect_FailsAsUpstream()
    {
        var http = new StubHttpHandler(HttpStatusCode.Found, string.Empty) { Headers = new Dictionary<string, string> { ["Location"] = "https://other.example.com/" } };

        var result = await Client(http).CompleteAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Upstream, result.Error?.Kind);
        Assert.Contains("302", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>The real network handler of the llama-server client does not follow redirects.</summary>
    [Fact]
    public void HttpClient_DoesNotFollowRedirects()
    {
        const string client = DependencyInjection.LocalHttpClientName;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalModel:RequestFields:0"] = "messages",
            ["QueryExpansionModel:RequestFields:0"] = "messages",
            ["QueryExpansionModel:RequestFields:1"] = "max_tokens",
        }).Build();
        using var services = new ServiceCollection().AddLogging().AddInfrastructure(configuration).BuildServiceProvider();

        HttpMessageHandler? handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(client);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    /// <summary>Gets the base address of the stubbed provider.</summary>
    private static Uri Address { get; } = new("https://api.example.com/v1");

    /// <summary>Builds a client over the stubbed network.</summary>
    /// <param name="http">The stubbed network.</param>
    /// <returns>The client.</returns>
    private static OpenAiClient Client(StubHttpHandler http) => new(TestTransports.Create(http));
}

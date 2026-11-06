using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.Providers;
using XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests how the shared transport handles broken connections, time limits and size limits.</summary>
public class ProviderTransportTests
{
    /// <summary>A short limit, so that the tests run fast.</summary>
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
        var http = new StubHttpHandler(HttpStatusCode.TooManyRequests, """{"error":""") { ThrowWhileReading = new IOException("reset") };

        var result = await Client(http).StreamAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.RateLimited, result.Error?.Kind);
    }

    /// <summary>A provider that never sends the headers fails when the time limit ends.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_NoHeaders_FailsAfterRequestTimeout()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, CompletionBody) { HangBeforeHeaders = true };

        var result = await Client(http, requestTimeout: ShortLimit).CompleteAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error?.Kind);
        Assert.Contains("did not start its answer", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A provider that sends the headers and then stops fails when the idle limit ends.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_StalledBody_FailsAfterIdleTimeout()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, """{"choices":[""") { StallAtEnd = true };

        var result = await Client(http, readIdleTimeout: ShortLimit).CompleteAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error?.Kind);
        Assert.Contains("sent no data", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A stream that stops without an end fails part-way, with a temporary failure.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_Stalls_FailsAfterIdleTimeout()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n") { StallAtEnd = true };
        var result = await Client(http, readIdleTimeout: ShortLimit).StreamAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);
        var chunks = new List<ChatChunk>();

        var failure = await Assert.ThrowsAsync<ChatStreamException>(async () =>
        {
            await foreach (var chunk in result.Value!)
            {
                chunks.Add(chunk);
            }
        });

        Assert.Equal("Hi", Assert.Single(chunks).Content);
        Assert.Equal(ErrorKind.Unavailable, failure.Error.Kind);
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
            Client(http, requestTimeout: TimeSpan.FromMinutes(1)).CompleteAsync(Address, "key", TestRequests.Minimal, cancel.Token));
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

    /// <summary>An authentication failure of the local server does not tell the user to correct a key, because the engine makes that key.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_LocalUnauthorized_GivesLocalAdvice()
    {
        var http = new StubHttpHandler(HttpStatusCode.Unauthorized, "{}");

        var result = await Client(http, kind: ProviderKind.Local).CompleteAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Upstream, result.Error?.Kind);
        Assert.DoesNotContain("API key", result.Error?.Message, StringComparison.Ordinal);
        Assert.Contains("Restart the engine", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A redirect is an error. The engine does not follow it, so a key such as x-api-key cannot go to another host.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_Redirect_FailsAsUpstream()
    {
        var http = new StubHttpHandler(HttpStatusCode.Found, string.Empty) { Headers = new Dictionary<string, string> { ["Location"] = "https://other.example.com/" } };

        var result = await Client(http).CompleteAsync(Address, "key", TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Upstream, result.Error?.Kind);
        Assert.Contains("302", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>The real network handler of every provider client does not follow redirects.</summary>
    /// <param name="client">The named client.</param>
    [Theory]
    [InlineData("provider-test")]
    [InlineData(DependencyInjection.LocalHttpClientName)]
    public void HttpClients_DoNotFollowRedirects(string client)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalModel:RequestFields:0"] = "messages",
            ["Providers:test:Type"] = "OpenAiCompatible",
            ["Providers:test:BaseUrl"] = "https://api.example.com",
            ["Providers:test:ApiKey"] = "secret",
            ["Providers:test:RequestFields:0"] = "messages",
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
    /// <param name="kind">The kind of provider.</param>
    /// <param name="requestTimeout">The limit for the headers.</param>
    /// <param name="readIdleTimeout">The idle limit for the body.</param>
    /// <returns>The client.</returns>
    private static OpenAiClient Client(StubHttpHandler http, ProviderKind kind = ProviderKind.Cloud, TimeSpan? requestTimeout = null, TimeSpan? readIdleTimeout = null) =>
        new(TestTransports.Create(http, kind: kind, requestTimeout: requestTimeout, readIdleTimeout: readIdleTimeout));
}

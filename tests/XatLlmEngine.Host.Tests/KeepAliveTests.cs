using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Host.Tests;

/// <summary>Tests the keep-alive comments while a stream waits for its first piece.</summary>
public class KeepAliveTests
{
    /// <summary>A stream request.</summary>
    private const string StreamBody = """{"model":"fake/model-a","stream":true,"messages":[{"role":"user","content":"Hi"}]}""";

    /// <summary>The longest wait for a step of the test.</summary>
    private static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(10);

    /// <summary>A slow first piece makes the engine send status 200 and keep-alive comments. The answer then follows.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_SlowFirstPiece_SendsKeepAlive()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var engine = new EngineFactory(timeProvider: time);
        engine.Provider.Chunks = [new ChatChunk("Hi", FinishReason.Stop, null)];
        engine.Provider.FirstChunkGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = engine.CreateClient();

        using var response = await StartAsync(client, engine, time);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        engine.Provider.FirstChunkGate.SetResult();
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith(": keep-alive", text, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"Hi\"", text, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", text, StringComparison.Ordinal);
    }

    /// <summary>A failure after the engine sent status 200 becomes an error event, with no end marker.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_FailsAfterKeepAlive_SendsErrorEvent()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var engine = new EngineFactory(timeProvider: time);
        engine.Provider.FirstChunkGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = engine.CreateClient();

        using var response = await StartAsync(client, engine, time);
        engine.Provider.FirstChunkGate.SetException(new ChatStreamException(new EngineError(ErrorKind.Unavailable, "The provider stopped.")));
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("service_unavailable_error", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[DONE]", text, StringComparison.Ordinal);
    }

    /// <summary>With a commit time of 0, the engine waits for the first piece, so a failure still gets its real status.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stream_CommitOff_WaitsForFirstPiece()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var engine = new EngineFactory(new Dictionary<string, string?> { ["Api:StreamCommitSeconds"] = "0" }, timeProvider: time);
        engine.Provider.FirstChunkGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = engine.CreateClient();
        var sending = Send(client);

        await engine.Provider.StreamStarted.Task.WaitAsync(StepLimit, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(sending.IsCompleted);
        engine.Provider.FirstChunkGate.SetException(new ChatStreamException(new EngineError(ErrorKind.Unavailable, "The provider stopped.")));

        using var response = await sending.WaitAsync(StepLimit, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>Sends the request, and moves the clock until the engine sends the headers.</summary>
    /// <param name="client">The client.</param>
    /// <param name="engine">The engine.</param>
    /// <param name="time">The clock.</param>
    /// <returns>The response, after its headers.</returns>
    private static async Task<HttpResponseMessage> StartAsync(HttpClient client, EngineFactory engine, FakeTimeProvider time)
    {
        var sending = Send(client);
        await engine.Provider.StreamStarted.Task.WaitAsync(StepLimit, TestContext.Current.CancellationToken);

        // The engine starts its timer a moment after the provider starts, so the clock moves in small steps.
        var started = DateTime.UtcNow;
        while (!sending.IsCompleted && DateTime.UtcNow - started < StepLimit)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return await sending.WaitAsync(StepLimit, TestContext.Current.CancellationToken);
    }

    /// <summary>Sends the stream request, and returns when the headers arrive.</summary>
    /// <param name="client">The client.</param>
    /// <returns>The response.</returns>
    private static Task<HttpResponseMessage> Send(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(StreamBody, Encoding.UTF8, "application/json"),
        };
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
    }
}

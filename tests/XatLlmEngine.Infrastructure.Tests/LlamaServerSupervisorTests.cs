using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.LlamaCpp;
using XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests process supervision by running a stand-in server as a real child process.</summary>
/// <remarks>Parallelization is off because every test uses the same program, and so the same leftover-process record.</remarks>
[CollectionDefinition(nameof(SupervisorTestGroup), DisableParallelization = true)]
public sealed class SupervisorTestGroup;

/// <summary>The supervisor tests.</summary>
[Collection(nameof(SupervisorTestGroup))]
public sealed class LlamaServerSupervisorTests : IDisposable
{
    /// <summary>The longest wait for a process to reach an expected stage.</summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    /// <summary>A file that stands in for the model.</summary>
    private readonly string modelFile = Path.GetTempFileName();

    /// <summary>Removes the stand-in model.</summary>
    public void Dispose() => File.Delete(modelFile);

    /// <summary>The arguments include the settings that protect memory and bind only to loopback.</summary>
    [Fact]
    public void BuildArguments_IncludesRequiredSettings()
    {
        var arguments = LlamaServerSupervisor.BuildArguments(new LlamaCppOptions { ContextSize = 2048, ExtraArguments = ["--threads", "2"] }, 1234);

        Assert.Equal("127.0.0.1", arguments[arguments.ToList().IndexOf("--host") + 1]);
        Assert.Equal("2048", arguments[arguments.ToList().IndexOf("--ctx-size") + 1]);
        Assert.Equal("1234", arguments[arguments.ToList().IndexOf("--port") + 1]);
        Assert.Contains("--no-webui", arguments);
        Assert.Equal(["--threads", "2"], arguments.TakeLast(2));
    }

    /// <summary>Requests go through the running server, using the random key created for this run.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_ServerHealthy_ServesRequests()
    {
        var (supervisor, provider, _) = Create();
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);

            var complete = await provider.CompleteAsync(TestRequests.Minimal with { Model = "test-model" }, TestContext.Current.CancellationToken);
            var stream = await provider.StreamAsync(TestRequests.Minimal with { Model = "test-model" }, TestContext.Current.CancellationToken);
            var chunks = await stream.Value!.ToListAsync(TestContext.Current.CancellationToken);

            Assert.Equal("fake answer", complete.Value!.Content);
            Assert.Equal("fake answer", string.Concat(chunks.Select(chunk => chunk.Content)));
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>While the model loads, the provider asks callers to retry, and it becomes ready without a restart.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_ModelLoading_ReportsUnavailableUntilReady()
    {
        var (supervisor, provider, _) = Create("--load-delay-ms", "1500");
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var whileLoading = await provider.CompleteAsync(TestRequests.Minimal with { Model = "test-model" }, TestContext.Current.CancellationToken);

            Assert.Equal(ErrorKind.Unavailable, whileLoading.Error!.Kind);
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The local provider checks the model name itself, because llama-server ignores it.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_UnknownLocalModel_FailsAsNotFound()
    {
        var (_, provider, _) = Create();

        var result = await provider.CompleteAsync(TestRequests.Minimal with { Model = "other" }, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
    }

    /// <summary>After a crash, the server comes back on a new port with a new key.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Run_ServerCrashes_RestartsWithNewEndpoint()
    {
        var (supervisor, _, _) = Create("--exit-after-ms", "1000");
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.Endpoint is not null);
            var first = supervisor.Endpoint;

            await WaitForAsync(() => supervisor.Endpoint is { } current && !Equals(current, first));

            Assert.NotEqual(first!.ApiKey, supervisor.Endpoint!.ApiKey);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>A server that fails at startup does not stop the engine, and it keeps being retried.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Run_ServerFailsAtStartup_KeepsRetrying()
    {
        var (supervisor, _, _) = Create("--fail-immediately");
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Restarting);

            Assert.Null(supervisor.Endpoint);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Heavy output cannot block the server, which proves that its output is drained.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_ServerWritesHeavyOutput_StillBecomesReady()
    {
        var (supervisor, _, _) = Create("--noisy-kilobytes", "2048");
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Stopping the engine stops the server, so no process keeps holding the model in memory.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Stop_RunningServer_EndsProcess()
    {
        var (supervisor, _, _) = Create();
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => supervisor.Endpoint is not null);
        var healthUrl = new Uri(supervisor.Endpoint!.BaseUrl, "/health");

        await supervisor.StopAsync(CancellationToken.None);

        Assert.Equal(LlamaServerState.Stopped, supervisor.State);
        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(healthUrl, TestContext.Current.CancellationToken));
    }

    /// <summary>A server left behind by a hard kill of the engine is stopped by the next start.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_LeftoverServerRecorded_StopsIt()
    {
        var program = LlamaServerPaths.ServerProgram(Options());
        using var leftover = Process.Start(new ProcessStartInfo(program, ["--port", "0"]) { RedirectStandardOutput = true })!;
        var pidFile = LlamaServerSupervisor.PidFilePath(program);
        Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
        await File.WriteAllTextAsync(pidFile, LlamaServerSupervisor.FormatPidRecord(leftover), TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(leftover.HasExited, "The stand-in must be running, or this test proves nothing.");

        var (supervisor, _, _) = Create();
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => leftover.HasExited);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
            if (!leftover.HasExited)
            {
                leftover.Kill();
            }
        }
    }

    /// <summary>An ID that now belongs to another program must not be killed.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_RecordedIdBelongsToOtherProgram_LeavesItRunning()
    {
        var program = LlamaServerPaths.ServerProgram(Options());
        var pidFile = LlamaServerSupervisor.PidFilePath(program);
        Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
        using var self = Process.GetCurrentProcess();
        await File.WriteAllTextAsync(pidFile, LlamaServerSupervisor.FormatPidRecord(self), TestContext.Current.CancellationToken);

        var (supervisor, _, _) = Create();
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // This test process is the recorded ID, so reaching this line alive is the assertion that matters.
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>A record whose start time does not match belongs to a later process with the same name, such as another program's llama-server after a reboot.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_SameNameButOtherStartTime_LeavesItRunning()
    {
        var program = LlamaServerPaths.ServerProgram(Options());
        using var unrelated = Process.Start(new ProcessStartInfo(program, ["--port", "0"]) { RedirectStandardOutput = true })!;
        var pidFile = LlamaServerSupervisor.PidFilePath(program);
        Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
        var oldStart = unrelated.StartTime.ToUniversalTime().AddHours(-1).Ticks;
        await File.WriteAllTextAsync(pidFile, $"{unrelated.Id} {oldStart}", TestContext.Current.CancellationToken);

        var (supervisor, _, _) = Create();
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);

            Assert.False(unrelated.HasExited);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
            unrelated.Kill();
        }
    }

    /// <summary>A running server that stops responding is replaced, because it would never exit by itself.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Run_ServerHangs_IsReplaced()
    {
        var options = new LlamaCppOptions
        {
            ModelName = "test-model",
            ModelPath = modelFile,
            ServerPath = Options().ServerPath,
            HealthCheckIntervalSeconds = 1,
            ExtraArguments = ["--unhealthy-after-ms", "500"],
        };
        var (supervisor, _) = Create(options);
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.Endpoint is not null);
            var first = supervisor.Endpoint;

            await WaitForAsync(() => supervisor.Endpoint is { } current && !Equals(current, first));
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The server does not receive the engine's own secrets, only the key for this run.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_EngineHasSecrets_DoesNotPassThemToServer()
    {
        Environment.SetEnvironmentVariable("Providers__test__ApiKey", "cloud-secret");
        var reportFile = Path.GetTempFileName();
        var (supervisor, _, _) = Create("--write-environment-to", reportFile);
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);
            var report = await File.ReadAllTextAsync(reportFile, TestContext.Current.CancellationToken);

            Assert.Contains("LLAMA_API_KEY", report, StringComparison.Ordinal);
            Assert.DoesNotContain("cloud-secret", report, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Providers__test__ApiKey", null);
            await supervisor.StopAsync(CancellationToken.None);
            File.Delete(reportFile);
        }
    }

    /// <summary>The local default temperature fills a gap, and never replaces the caller's value.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="expected">The temperature that should reach llama-server.</param>
    /// <returns>A task for the test.</returns>
    [Theory]
    [InlineData("""{"messages":[]}""", 0.07)]
    [InlineData("""{"temperature":0.9,"messages":[]}""", 0.9)]
    public async Task Complete_Temperature_UsesDefaultOnlyWhenMissing(string body, double expected)
    {
        var options = Options();
        var (supervisor, _) = Create(options);
        var http = new StubHttpHandler(System.Net.HttpStatusCode.OK, """{"choices":[{"message":{"content":"Hi"},"finish_reason":"stop"}]}""");
        var provider = new LocalChatProvider(supervisor, new OpenAiClient("local", http, "test"), Microsoft.Extensions.Options.Options.Create(options));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);

            var request = TestRequests.Parse(body) with { Model = "test-model" };
            await provider.CompleteAsync(request, TestContext.Current.CancellationToken);

            using var sent = System.Text.Json.JsonDocument.Parse(http.LastBody!);
            Assert.Equal(expected, sent.RootElement.GetProperty("temperature").GetDouble());
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>A missing model leaves the provider off, with a message that says what to do.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_ModelMissing_ReportsMissing()
    {
        var (supervisor, provider) = Create(new LlamaCppOptions
        {
            ModelName = "test-model",
            ModelPath = Path.Combine(AppContext.BaseDirectory, "does-not-exist.gguf"),
            ServerPath = Options().ServerPath,
        });
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => supervisor.State == LlamaServerState.Missing);
        await supervisor.StopAsync(CancellationToken.None);

        var result = await provider.CompleteAsync(TestRequests.Minimal with { Model = "test-model" }, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error!.Kind);
        Assert.Contains("not installed", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>Waits for a condition on a running process.</summary>
    /// <param name="condition">The condition.</param>
    /// <returns>A task that fails when the limit passes first.</returns>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(started.Elapsed < WaitLimit, "The expected state was not reached in time.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Builds settings that run the stand-in server.</summary>
    /// <param name="extraArguments">Arguments that select the stand-in's behaviour.</param>
    /// <returns>The settings.</returns>
    private LlamaCppOptions Options(params string[] extraArguments) => new()
    {
        ModelName = "test-model",
        ModelPath = modelFile,
        ServerPath = Path.Combine(AppContext.BaseDirectory, "XatLlmEngine.FakeLlamaServer"),
        ExtraArguments = extraArguments,
    };

    /// <summary>Creates the supervisor and local provider.</summary>
    /// <param name="extraArguments">Arguments that select the stand-in's behaviour.</param>
    /// <returns>The objects under test.</returns>
    private (LlamaServerSupervisor Supervisor, LocalChatProvider Provider, LlamaCppOptions Options) Create(params string[] extraArguments)
    {
        var options = Options(extraArguments);
        var (supervisor, provider) = Create(options);
        return (supervisor, provider, options);
    }

    /// <summary>Creates the supervisor and local provider.</summary>
    /// <param name="options">The settings.</param>
    /// <returns>The objects under test.</returns>
    private static (LlamaServerSupervisor Supervisor, LocalChatProvider Provider) Create(LlamaCppOptions options)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(DependencyInjection.LocalHttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var supervisor = new LlamaServerSupervisor(wrapped, factory, TimeProvider.System, NullLogger<LlamaServerSupervisor>.Instance);
        var client = new OpenAiClient("local", factory, DependencyInjection.LocalHttpClientName);
        return (supervisor, new LocalChatProvider(supervisor, client, wrapped));
    }
}

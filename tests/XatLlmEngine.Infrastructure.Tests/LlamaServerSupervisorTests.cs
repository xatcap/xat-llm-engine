using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.LlamaCpp;
using XatLlmEngine.Infrastructure.Providers;
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

    /// <summary>The data folder of this test, so that tests do not share the PID file of the real install.</summary>
    private readonly string dataFolder = Directory.CreateTempSubdirectory().FullName;

    /// <summary>The request gate of each supervisor that this test created.</summary>
    private readonly Dictionary<LlamaServerSupervisor, LocalRequestGate> gates = [];

    /// <summary>Removes the stand-in model and the data folder.</summary>
    public void Dispose()
    {
        File.Delete(modelFile);
        Directory.Delete(dataFolder, recursive: true);
    }

    /// <summary>The arguments include the settings that protect memory and bind only to loopback.</summary>
    [Fact]
    public void BuildArguments_IncludesRequiredSettings()
    {
        var arguments = LlamaServerArguments.Build(new LlamaCppOptions { ContextSize = 2048, ExtraArguments = ["--threads", "2"] }, 1234);

        Assert.Equal("127.0.0.1", arguments[arguments.ToList().IndexOf("--host") + 1]);
        Assert.Equal("2048", arguments[arguments.ToList().IndexOf("--ctx-size") + 1]);
        Assert.Equal("1234", arguments[arguments.ToList().IndexOf("--port") + 1]);
        Assert.Contains("--no-webui", arguments);
        Assert.Equal(["--threads", "2"], arguments.TakeLast(2));
    }

    /// <summary>A context size in the extra arguments replaces the setting, so llama-server gets the flag one time only.</summary>
    [Fact]
    public void BuildArguments_ExtraContextSize_ReplacesSetting()
    {
        var arguments = LlamaServerArguments.Build(new LlamaCppOptions { ContextSize = 2048, ExtraArguments = ["-c", "4096"] }, 1234);

        Assert.DoesNotContain("--ctx-size", arguments);
        Assert.DoesNotContain("2048", arguments);
        Assert.Single(arguments, argument => argument == "-c");
        Assert.Equal(["-c", "4096"], arguments.TakeLast(2));
    }

    /// <summary>GPU layers and parallel slots in the extra arguments replace the settings, also when they use another spelling.</summary>
    [Fact]
    public void BuildArguments_ExtraGpuLayersAndParallel_ReplaceSettings()
    {
        var arguments = LlamaServerArguments.Build(new LlamaCppOptions { ExtraArguments = ["--n_gpu_layers", "0", "-np", "2"] }, 1234);

        Assert.DoesNotContain("--gpu-layers", arguments);
        Assert.DoesNotContain("--parallel", arguments);
        Assert.Contains("--ctx-size", arguments);
        Assert.Equal(["--n_gpu_layers", "0", "-np", "2"], arguments.TakeLast(4));
    }

    /// <summary>A value never counts as a flag, so the engine still adds its own context size.</summary>
    [Fact]
    public void BuildArguments_ValueLooksLikeFlag_KeepsSetting()
    {
        var arguments = LlamaServerArguments.Build(new LlamaCppOptions { ContextSize = 2048, ExtraArguments = ["--threads", "-c"] }, 1234);

        Assert.Equal("2048", arguments[arguments.ToList().IndexOf("--ctx-size") + 1]);
    }

    /// <summary>The engine finds the same config files as llama.cpp on Linux and macOS.</summary>
    /// <param name="xdgConfigHome">The value of XDG_CONFIG_HOME.</param>
    /// <param name="home">The value of HOME.</param>
    /// <param name="expectedFolder">The folder that holds the user's file.</param>
    [Theory]
    [InlineData("/x/config", "/home/u", "/x/config")]
    [InlineData("", "/home/u", "/home/u/.config")]
    [InlineData(null, "/home/u", "/home/u/.config")]
    public void ConfigFiles_Unix_FollowsLlamaRules(string? xdgConfigHome, string home, string expectedFolder)
    {
        var files = LlamaServerPaths.ConfigFiles(isWindows: false, name => name switch
        {
            "XDG_CONFIG_HOME" => xdgConfigHome,
            "HOME" => home,
            _ => null,
        });

        Assert.Equal([Path.Combine("/etc", "llama.cpp", "config.ini"), Path.Combine(expectedFolder, "llama.cpp", "config.ini")], files);
    }

    /// <summary>The engine finds the same config files as llama.cpp on Windows, and skips a folder whose variable is not set.</summary>
    [Fact]
    public void ConfigFiles_Windows_FollowsLlamaRules()
    {
        var both = LlamaServerPaths.ConfigFiles(isWindows: true, name => name switch
        {
            "PROGRAMDATA" => "P",
            "APPDATA" => "A",
            _ => null,
        });
        var none = LlamaServerPaths.ConfigFiles(isWindows: true, _ => "");

        Assert.Equal([Path.Combine("P", "llama.cpp", "config.ini"), Path.Combine("A", "llama.cpp", "config.ini")], both);
        Assert.Empty(none);
    }

    /// <summary>A llama.cpp config file makes the local model fail, because llama-server would read it. The engine itself still starts, so cloud providers continue to work.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_ConfigFileExists_FailsLocalModelOnly()
    {
        var variable = OperatingSystem.IsWindows() ? "APPDATA" : "XDG_CONFIG_HOME";
        var previous = Environment.GetEnvironmentVariable(variable);
        var folder = Directory.CreateTempSubdirectory().FullName;
        var (supervisor, provider, _) = Create();
        try
        {
            var configFile = Path.Combine(folder, "llama.cpp", "config.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            await File.WriteAllTextAsync(configFile, "log-file = x.log", TestContext.Current.CancellationToken);
            Environment.SetEnvironmentVariable(variable, folder);

            await supervisor.StartAsync(TestContext.Current.CancellationToken);
            await WaitForAsync(() => supervisor.State == LlamaServerState.Failed);
            var result = await provider.CompleteAsync(TestRequests.Minimal with { Model = "test-model" }, TestContext.Current.CancellationToken);

            // A system-wide file on this machine is found first, so the test checks only the file name.
            Assert.Contains("config.ini", supervisor.Snapshot.Failure!.Detail, StringComparison.Ordinal);
            Assert.Equal(ErrorKind.Upstream, result.Error!.Kind);
            Assert.Contains("config file", result.Error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(folder, result.Error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable(variable, previous);
            Directory.Delete(folder, recursive: true);
        }
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
            Assert.Equal(0, GateOf(supervisor).Active);

            Assert.Equal("fake answer", complete.Value!.Content);
            Assert.Equal("piece0 piece1 ", string.Concat(chunks.Select(chunk => chunk.Content)));
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

    /// <summary>A server that fails at startup does not stop the engine. The engine does not start it again, and it keeps the cause.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Run_ServerFailsAtStartup_FailsWithoutRetry()
    {
        var startRecord = Path.GetTempFileName();
        var (supervisor, _, _) = Create("--record-start-to", startRecord, "--fail-immediately");
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Failed);

            Assert.Null(supervisor.Endpoint);
            Assert.Contains("fake failure", supervisor.Snapshot.Failure!.Detail, StringComparison.Ordinal);
            Assert.Contains("exit code 3", supervisor.Snapshot.Failure.Detail, StringComparison.Ordinal);

            // A retry would start the server again after 1 second, so wait longer than that.
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Single(await File.ReadAllLinesAsync(startRecord, TestContext.Current.CancellationToken));
            Assert.Equal(LlamaServerState.Failed, supervisor.State);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
            File.Delete(startRecord);
        }

        Assert.Equal(LlamaServerState.Failed, supervisor.State);
    }

    /// <summary>A local request gets the cause of a failed start, as an upstream error that a retry does not correct.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Complete_ServerFailed_ReturnsCause()
    {
        var (supervisor, provider, _) = Create("--fail-immediately");
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Failed);

            var result = await provider.CompleteAsync(TestRequests.Minimal with { Model = "test-model" }, TestContext.Current.CancellationToken);

            // The caller gets the exit code. The server output can hold file paths, so only the engine log gets it.
            Assert.Equal(ErrorKind.Upstream, result.Error!.Kind);
            Assert.Contains("exit code 3", result.Error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("fake failure", result.Error.Message, StringComparison.Ordinal);
            Assert.Null(result.Error.RetryAfter);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>A server that crashes again and again is not restarted for ever. After three crashes, the local model fails.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Run_ServerCrashesThreeTimes_Fails()
    {
        // The server must live long enough for a health check, so that each run counts as a crash of a ready server.
        var (supervisor, _, _) = Create("--exit-after-ms", "1000");
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Failed);

            Assert.Contains("stopped 3 times", supervisor.Snapshot.Failure!.PublicMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("correct the settings", supervisor.Snapshot.Failure.PublicMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Much output cannot block the server, which proves that the engine reads all its output.</summary>
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

    /// <summary>The next engine start stops a server that remains after the engine was killed.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_LeftoverServerRecorded_StopsIt()
    {
        var program = LlamaServerPaths.ServerProgram(Options());
        using var leftover = Process.Start(new ProcessStartInfo(program, ["--port", "0"]) { RedirectStandardOutput = true })!;
        var pidFile = new EngineDataFolder(dataFolder).File(LlamaServerPidFile.FileName);
        await File.WriteAllTextAsync(pidFile, FormatPidRecord(leftover), TestContext.Current.CancellationToken);
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

    /// <summary>The engine must not kill an ID that now belongs to another program.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_RecordedIdBelongsToOtherProgram_LeavesItRunning()
    {
        var program = LlamaServerPaths.ServerProgram(Options());
        var pidFile = new EngineDataFolder(dataFolder).File(LlamaServerPidFile.FileName);
        using var self = Process.GetCurrentProcess();
        await File.WriteAllTextAsync(pidFile, FormatPidRecord(self), TestContext.Current.CancellationToken);

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
        var pidFile = new EngineDataFolder(dataFolder).File(LlamaServerPidFile.FileName);
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

    /// <summary>The engine replaces a running server that stops responding, because that server never exits by itself.</summary>
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

    /// <summary>The server does not receive the engine's own secrets or llama.cpp settings, only the key for this run.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_EngineHasSecrets_DoesNotPassThemToServer()
    {
        string[] llamaSettings =
        [
            "LLAMA_ARG_LOG_FILE", "LLAMA_ARG_NO_WARMUP", "LLAMA_TRACE", "HF_TOKEN", "MTMD_BACKEND_DEVICE",
            "GGML_BACKEND_PATH", "AIP_MODE", "https_proxy", "MODEL_ENDPOINT",
        ];
        var previous = llamaSettings.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var reportFile = Path.GetTempFileName();
        var (supervisor, _, _) = Create("--write-environment-to", reportFile);
        try
        {
            Environment.SetEnvironmentVariable("Providers__test__ApiKey", "cloud-secret");
            foreach (var name in llamaSettings)
            {
                Environment.SetEnvironmentVariable(name, "llama-setting");
            }

            await supervisor.StartAsync(TestContext.Current.CancellationToken);
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);
            var report = await File.ReadAllTextAsync(reportFile, TestContext.Current.CancellationToken);

            Assert.Contains("LLAMA_API_KEY", report, StringComparison.Ordinal);
            Assert.DoesNotContain("cloud-secret", report, StringComparison.Ordinal);
            Assert.DoesNotContain("llama-setting", report, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Providers__test__ApiKey", null);
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

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
        var provider = new LocalChatProvider(supervisor, GateOf(supervisor), new OpenAiClient(TestTransports.Create(http, name: "local", kind: ProviderKind.Local)), Microsoft.Extensions.Options.Options.Create(options));
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

        Assert.Equal(ErrorKind.Upstream, result.Error!.Kind);
        Assert.Contains("not installed", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>Many requests at once reach llama-server one per slot. The others wait in the engine and then succeed.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Load_ManyRequests_OnePerSlot()
    {
        var record = Path.GetTempFileName();
        var (supervisor, provider) = Create(Tuned(queueLimit: 10, "--complete-delay-ms", "200", "--record-concurrency-to", record));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);

            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
                provider.CompleteAsync(TestRequests.Minimal with { Model = "test-model" }, TestContext.Current.CancellationToken)));

            Assert.All(results, result => Assert.True(result.IsSuccess));
            Assert.Equal("1", await File.ReadAllTextAsync(record, TestContext.Current.CancellationToken));
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
            File.Delete(record);
        }
    }

    /// <summary>A request that finds the queue full gets 503 at once, and the requests in the queue still succeed.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Load_QueueFull_RefusesExtraRequests()
    {
        var (supervisor, provider) = Create(Tuned(queueLimit: 1, "--complete-delay-ms", "500"));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);

            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                provider.CompleteAsync(TestRequests.Minimal with { Model = "test-model" }, TestContext.Current.CancellationToken)));

            Assert.Equal(2, results.Count(result => result.IsSuccess));
            Assert.Equal(2, results.Count(result => result.Error?.Kind == ErrorKind.Overloaded));
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>A busy server that fails its health checks is not stopped while it sends data. New requests wait until it answers again.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Load_BusyServerFailsHealth_IsNotStopped()
    {
        var (supervisor, provider) = Create(Tuned(queueLimit: 4, "--health-fails-while-busy", "--stream-chunks", "16", "--chunk-delay-ms", "300"));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);
            var first = supervisor.Endpoint;
            var stream = await provider.StreamAsync(TestRequests.Minimal with { Model = "test-model" }, TestContext.Current.CancellationToken);
            var reading = stream.Value!.ToListAsync(TestContext.Current.CancellationToken).AsTask();

            await WaitForAsync(() => supervisor.State == LlamaServerState.Unresponsive);
            var held = await provider.CompleteAsync(TestRequests.Minimal with { Model = "test-model" }, TestContext.Current.CancellationToken);
            var chunks = await reading;

            Assert.Equal(ErrorKind.Unavailable, held.Error?.Kind);
            Assert.Equal(16, chunks.Count);
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);
            Assert.Equal(first, supervisor.Endpoint);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>A busy server that fails its health checks and sends no data for the unresponsive time is replaced. The stuck request fails.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Load_BusyServerStalls_IsReplaced()
    {
        var (supervisor, provider) = Create(Tuned(queueLimit: 4, "--health-fails-while-busy", "--stream-chunks", "4", "--stall-stream-after-chunks", "1"));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => supervisor.State == LlamaServerState.Ready);
            var first = supervisor.Endpoint;
            var stream = await provider.StreamAsync(TestRequests.Minimal with { Model = "test-model" }, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<XatLlmEngine.Application.Abstractions.ChatStreamException>(async () => await stream.Value!.ToListAsync(TestContext.Current.CancellationToken));

            await WaitForAsync(() => supervisor.Endpoint is { } current && !Equals(current, first));
            Assert.Equal(0, GateOf(supervisor).Active);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Builds settings with short times for the load tests. The supervisor gets them directly, so the ranges of the settings do not apply.</summary>
    /// <param name="queueLimit">The number of requests that can wait.</param>
    /// <param name="extraArguments">Arguments that select the stand-in's behaviour.</param>
    /// <returns>The settings.</returns>
    private LlamaCppOptions Tuned(int queueLimit, params string[] extraArguments) => new()
    {
        ModelName = "test-model",
        ModelPath = modelFile,
        ServerPath = Options().ServerPath,
        HealthCheckIntervalSeconds = 1,
        UnresponsiveTimeoutSeconds = 2,
        MaxQueuedRequests = queueLimit,
        ExtraArguments = extraArguments,
    };

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
    private (LlamaServerSupervisor Supervisor, LocalChatProvider Provider) Create(LlamaCppOptions options)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(DependencyInjection.LocalHttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var requestGate = new LocalRequestGate(options.EffectiveParallel, options.MaxQueuedRequests, TimeProvider.System);
        var supervisor = new LlamaServerSupervisor(wrapped, factory, requestGate, new EngineDataFolder(dataFolder), TimeProvider.System, NullLogger<LlamaServerSupervisor>.Instance);
        gates[supervisor] = requestGate;
        var client = new OpenAiClient(new ProviderTransport(
            new TransportSettings("local", ProviderKind.Local, DependencyInjection.LocalHttpClientName, RequestTimeout: null, ReadIdleTimeout: null),
            factory,
            TimeProvider.System,
            NullLogger.Instance));
        return (supervisor, new LocalChatProvider(supervisor, requestGate, client, wrapped));
    }

    /// <summary>Gets the request gate of a supervisor.</summary>
    /// <param name="supervisor">The supervisor.</param>
    /// <returns>The gate.</returns>
    private LocalRequestGate GateOf(LlamaServerSupervisor supervisor) => gates[supervisor];

    /// <summary>Builds the text of a PID file for a process.</summary>
    /// <param name="process">The process.</param>
    /// <returns>The text.</returns>
    private static string FormatPidRecord(Process process) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{process.Id} {process.StartTime.ToUniversalTime().Ticks}");
}

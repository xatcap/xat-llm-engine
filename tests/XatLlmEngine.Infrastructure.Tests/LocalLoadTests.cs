using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.LlamaCpp;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the request gate, the hang rules, the crash budget, the instance lock and the llama.cpp version check, without a server.</summary>
public class LocalLoadTests
{
    /// <summary>The state of a server that can take requests.</summary>
    private static readonly LlamaServerSnapshot ReadyServer = new(LlamaServerState.Ready, new LlamaServerEndpoint(new Uri("http://127.0.0.1:1/v1"), "key"));

    /// <summary>The gate is closed until the model is ready, and a request then gets the loading error at once.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Gate_BeforeReady_RefusesWithLoadingError()
    {
        var gate = new LocalRequestGate(1, 4, TimeProvider.System);

        var result = await gate.EnterAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error?.Kind);
        Assert.Contains("loading", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>Waiting requests get the free slot in the order in which they arrived.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Gate_SlotFree_GoesToFirstWaiter()
    {
        var gate = Open(capacity: 1, queueLimit: 4);
        var first = (await gate.EnterAsync(TestContext.Current.CancellationToken)).Value!;
        var second = gate.EnterAsync(TestContext.Current.CancellationToken);
        var third = gate.EnterAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, gate.Queued);
        first.Dispose();
        var secondLease = (await second).Value!;

        Assert.False(third.IsCompleted);
        Assert.Equal(1, gate.Active);
        secondLease.Dispose();
        (await third).Value!.Dispose();
        Assert.Equal(0, gate.Active);
    }

    /// <summary>A request that finds all slots busy and the queue full gets 503 at once.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Gate_QueueFull_RefusesAsOverloaded()
    {
        var gate = Open(capacity: 1, queueLimit: 1);
        using var running = (await gate.EnterAsync(TestContext.Current.CancellationToken)).Value!;
        _ = gate.EnterAsync(TestContext.Current.CancellationToken);

        var refused = await gate.EnterAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Overloaded, refused.Error?.Kind);
    }

    /// <summary>A caller that disconnects while it waits leaves the queue, and does not take a slot later.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Gate_WaiterCancels_LeavesQueue()
    {
        var gate = Open(capacity: 1, queueLimit: 4);
        var running = (await gate.EnterAsync(TestContext.Current.CancellationToken)).Value!;
        using var cancel = new CancellationTokenSource();
        var waiting = gate.EnterAsync(cancel.Token);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, gate.Queued);
        running.Dispose();
        Assert.Equal(0, gate.Active);
    }

    /// <summary>When the server stops serving, the waiting requests get the error at once. The request with a slot continues.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Gate_ServerStops_FailsWaiters()
    {
        var gate = Open(capacity: 1, queueLimit: 4);
        using var running = (await gate.EnterAsync(TestContext.Current.CancellationToken)).Value!;
        var waiting = gate.EnterAsync(TestContext.Current.CancellationToken);

        gate.Update(new LlamaServerSnapshot(LlamaServerState.Restarting));

        Assert.Equal(ErrorKind.Unavailable, (await waiting).Error?.Kind);
        Assert.Equal(1, gate.Active);
    }

    /// <summary>A lease frees its slot only once, also when the caller disposes it two times.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Gate_LeaseDisposedTwice_FreesOneSlot()
    {
        var gate = Open(capacity: 2, queueLimit: 0);
        var first = (await gate.EnterAsync(TestContext.Current.CancellationToken)).Value!;
        using var second = (await gate.EnterAsync(TestContext.Current.CancellationToken)).Value!;

        first.Dispose();
        first.Dispose();

        Assert.Equal(1, gate.Active);
    }

    /// <summary>A healthy check lets the server serve. A few failed checks change nothing.</summary>
    [Fact]
    public void HangWatch_FewFailures_Waits()
    {
        var watch = new HangWatch(new FakeTimeProvider(), Open(1, 1), TimeSpan.FromMinutes(5));

        Assert.Equal(HangDecision.Wait, watch.Record(healthy: false));
        Assert.Equal(HangDecision.Wait, watch.Record(healthy: false));
        Assert.Equal(HangDecision.Serve, watch.Record(healthy: true));
        Assert.Equal(HangDecision.Wait, watch.Record(healthy: false));
    }

    /// <summary>Three failed checks while no request runs mean that the server hangs.</summary>
    [Fact]
    public void HangWatch_FailsWhileIdle_Restarts()
    {
        var watch = new HangWatch(new FakeTimeProvider(), Open(1, 1), TimeSpan.FromMinutes(5));

        watch.Record(healthy: false);
        watch.Record(healthy: false);

        Assert.Equal(HangDecision.Restart, watch.Record(healthy: false));
    }

    /// <summary>Three failed checks while a request runs only hold new requests. The server is not stopped while it sends data.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task HangWatch_FailsWhileBusyWithData_HoldsNewRequests()
    {
        var time = new FakeTimeProvider();
        var gate = Open(1, 1, time);
        using var running = (await gate.EnterAsync(TestContext.Current.CancellationToken)).Value!;
        var watch = new HangWatch(time, gate, TimeSpan.FromMinutes(5));

        for (var check = 0; check < 60; check++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            gate.ReportProgress();
            var decision = watch.Record(healthy: false);
            Assert.NotEqual(HangDecision.Restart, decision);
        }

        Assert.Equal(HangDecision.HoldNewRequests, watch.Record(healthy: false));
    }

    /// <summary>A busy server that fails its checks and sends no data for the unresponsive time hangs.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task HangWatch_FailsWhileBusyWithoutData_RestartsAfterTimeout()
    {
        var time = new FakeTimeProvider();
        var gate = Open(1, 1, time);
        using var running = (await gate.EnterAsync(TestContext.Current.CancellationToken)).Value!;
        var watch = new HangWatch(time, gate, TimeSpan.FromSeconds(60));

        watch.Record(healthy: false);
        watch.Record(healthy: false);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HangDecision.HoldNewRequests, watch.Record(healthy: false));
        time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(HangDecision.Restart, watch.Record(healthy: false));
    }

    /// <summary>The third crash in ten minutes uses up the budget. Crashes that are older than ten minutes do not count.</summary>
    [Fact]
    public void CrashBudget_ThreeCrashesInWindow_IsUsedUp()
    {
        var time = new FakeTimeProvider();
        var budget = new CrashBudget(time);

        Assert.False(budget.RecordCrash());
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(budget.RecordCrash());
        Assert.False(budget.RecordCrash());
        Assert.True(budget.RecordCrash());
    }

    /// <summary>The effective number of slots is the last valid parallel flag in the extra arguments, or else the setting.</summary>
    /// <param name="arguments">The extra arguments, separated by '|'.</param>
    /// <param name="expected">The expected number of slots.</param>
    [Theory]
    [InlineData("", 3)]
    [InlineData("-np|2", 2)]
    [InlineData("--parallel|4|--threads|2", 4)]
    [InlineData("-np|2|--parallel|5", 5)]
    public void EffectiveParallel_ReadsExtraArguments(string arguments, int expected)
    {
        var options = new LlamaCppOptions { Parallel = 3, ExtraArguments = arguments.Split('|', StringSplitOptions.RemoveEmptyEntries) };

        Assert.Equal(expected, options.EffectiveParallel);
    }

    /// <summary>The engine leaves the HTTP threads to llama-server, which keeps more threads than slots. A value in the extra arguments still reaches it.</summary>
    [Fact]
    public void Arguments_HttpThreads_LeftToServer()
    {
        var automatic = LlamaServerArguments.Build(new LlamaCppOptions { Parallel = 2 }, 1);
        var manual = LlamaServerArguments.Build(new LlamaCppOptions { ExtraArguments = ["--threads-http", "9"] }, 1);

        Assert.DoesNotContain("--threads-http", automatic);
        Assert.Single(manual, argument => argument == "--threads-http");
    }

    /// <summary>A second engine from the same install folder cannot take the lock. After the first engine stops, it can.</summary>
    [Fact]
    public void InstanceLock_SecondEngine_Fails()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var dataFolder = new EngineDataFolder(folder);
            var first = new EngineInstanceLock(dataFolder);
            first.Acquire();

            using var second = new EngineInstanceLock(dataFolder);
            var failure = Assert.Throws<InvalidOperationException>(second.Acquire);
            Assert.Contains("Another engine", failure.Message, StringComparison.Ordinal);

            first.Dispose();
            second.Acquire();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The pinned llama.cpp build is the build that the engine's flag, variable and file rules were checked against.</summary>
    [Fact]
    public void LlamaCpp_PinnedTag_MatchesVerifiedTag()
    {
        using var lockFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "llama-cpp.lock.json")));
        var pinned = lockFile.RootElement.GetProperty("tag").GetString();

        Assert.True(
            pinned == LlamaCppCompatibility.VerifiedTag,
            $"build/llama-cpp.lock.json pins {pinned}, but the rules were checked against {LlamaCppCompatibility.VerifiedTag}. "
            + "Check LlamaCppOptions.AllowedFlags, LlamaEnvironment.IsLlamaSetting, LlamaServerPaths.ConfigFiles and the /health and /props routes against the new source. Then change LlamaCppCompatibility.VerifiedTag.");
    }

    /// <summary>
    /// A long program name matches its full name on every system. On Linux, .NET can also report the name cut to 15
    /// characters, so that form matches there. No other part of the name, and no other name, matches.
    /// </summary>
    [Fact]
    public void PidFile_LongProgramName_Matches()
    {
        const string Program = "/opt/engine/llama/llama-server-with-long-name";

        Assert.True(LlamaServerPidFile.HasProgramName("llama-server-with-long-name", Program));
        Assert.Equal(OperatingSystem.IsLinux(), LlamaServerPidFile.HasProgramName("llama-server-wi", Program));
        Assert.False(LlamaServerPidFile.HasProgramName("llama-server-wX", Program));
        Assert.False(LlamaServerPidFile.HasProgramName("llama-server", Program));
        Assert.False(LlamaServerPidFile.HasProgramName("llama", Program));
        Assert.False(LlamaServerPidFile.HasProgramName("bash", Program));
    }

    /// <summary>Builds a gate for a ready server.</summary>
    /// <param name="capacity">The number of slots.</param>
    /// <param name="queueLimit">The number of requests that can wait.</param>
    /// <param name="timeProvider">The clock, or <see langword="null"/> for the system clock.</param>
    /// <returns>The gate.</returns>
    private static LocalRequestGate Open(int capacity, int queueLimit, TimeProvider? timeProvider = null)
    {
        var gate = new LocalRequestGate(capacity, queueLimit, timeProvider ?? TimeProvider.System);
        gate.Update(ReadyServer);
        return gate;
    }
}

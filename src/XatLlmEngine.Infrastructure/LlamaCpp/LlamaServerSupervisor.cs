using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Starts llama-server, watches it, and restarts it when it stops unexpectedly.</summary>
/// <remarks>
/// llama.cpp runs as a separate process, so a native crash cannot stop the API (see ADR 0001).
/// <see cref="Snapshot"/> is safe to read from any thread. The hang policy is in ADR 0009.
/// </remarks>
internal sealed partial class LlamaServerSupervisor : BackgroundService
{
    /// <summary>The delay before a restart after a crash.</summary>
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);

    /// <summary>The wait between checks while the model loads.</summary>
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How many health checks in a row must fail before a server counts as not responding.</summary>
    internal const int FailedChecksLimit = 3;

    /// <summary>The local model settings.</summary>
    private readonly LlamaCppOptions options;

    /// <summary>The identity of the model that this supervisor runs.</summary>
    private readonly LlamaModelRole role;

    /// <summary>Checks the health of the server.</summary>
    private readonly LlamaHealthProbe probe;

    /// <summary>The gate for local requests. It tells whether requests are in progress, and it gets each change of state.</summary>
    private readonly LocalRequestGate gate;

    /// <summary>The engine's data folder, for the PID file.</summary>
    private readonly EngineDataFolder dataFolder;

    /// <summary>The clock for delays, which tests can replace.</summary>
    private readonly TimeProvider timeProvider;

    /// <summary>The log for lifecycle events.</summary>
    private readonly ILogger<LlamaServerSupervisor> logger;

    /// <summary>The meter of the local model, or <see langword="null"/> when nothing collects metrics.</summary>
    private readonly Meter? meter;

    /// <summary>Counts the restarts of llama-server, by cause.</summary>
    private readonly Counter<long>? restarts;

    /// <summary>The current state. The supervisor replaces the whole snapshot, so the state, the address and the failure always agree.</summary>
    private volatile LlamaServerSnapshot snapshot = new(LlamaServerState.Starting);

    /// <summary>Initializes a new instance of the <see cref="LlamaServerSupervisor"/> class.</summary>
    /// <param name="options">The local model settings.</param>
    /// <param name="httpClientFactory">Creates the client for health checks.</param>
    /// <param name="gate">The gate for local requests.</param>
    /// <param name="dataFolder">The engine's data folder.</param>
    /// <param name="timeProvider">The clock for delays.</param>
    /// <param name="logger">The log for lifecycle events.</param>
    /// <param name="meterFactory">Creates the meter, or <see langword="null"/> when nothing collects metrics.</param>
    /// <param name="role">The identity of the model, or <see langword="null"/> for the main model.</param>
    public LlamaServerSupervisor(
        IOptions<LlamaCppOptions> options,
        IHttpClientFactory httpClientFactory,
        LocalRequestGate gate,
        EngineDataFolder dataFolder,
        TimeProvider timeProvider,
        ILogger<LlamaServerSupervisor> logger,
        IMeterFactory? meterFactory = null,
        LlamaModelRole? role = null)
    {
        this.options = options.Value;
        this.role = role ?? LlamaModelRole.Main;
        probe = new LlamaHealthProbe(httpClientFactory);
        this.gate = gate;
        this.dataFolder = dataFolder;
        this.timeProvider = timeProvider;
        this.logger = logger;
        if (meterFactory is null)
        {
            return;
        }

        meter = meterFactory.Create(MeterName);
        restarts = meter.CreateCounter<long>("xat_engine.local.restarts", "{restart}", "Restarts of llama-server, by model and by cause: crash or hang.");
        var modelTag = new KeyValuePair<string, object?>("model", this.role.Label);
        meter.CreateObservableGauge("xat_engine.local.state", () => new Measurement<int>(1, modelTag, new KeyValuePair<string, object?>("state", StateTag(snapshot.State))), description: "The state of a local model. The value is 1, with the model and the state as tags.");
        meter.CreateObservableGauge("xat_engine.local.active", () => new Measurement<int>(gate.Active, modelTag), "{request}", "Local requests that have a slot, by model.");
        meter.CreateObservableGauge("xat_engine.local.queued", () => new Measurement<int>(gate.Queued, modelTag), "{request}", "Local requests that wait for a slot, by model.");
    }

    /// <summary>The name of the meter. It is the same as the Application meter, so that one tool session shows all values.</summary>
    internal const string MeterName = "XatLlmEngine";

    /// <summary>Gets the current state.</summary>
    public LlamaServerSnapshot Snapshot => snapshot;

    /// <summary>Gets the current stage.</summary>
    public LlamaServerState State => snapshot.State;

    /// <summary>Gets the address of the server, or <see langword="null"/> when it cannot serve requests.</summary>
    public LlamaServerEndpoint? Endpoint => snapshot.Endpoint;

    /// <inheritdoc/>
    /// <remarks>
    /// A failed start never causes a retry, because a wrong setting does not correct itself. Local requests then get the cause.
    /// A crash of a ready server causes a restart, but <see cref="CrashBudget.MaxCrashes"/> crashes in <see cref="CrashBudget.Window"/> stop the restarts.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var program = LlamaServerPaths.ServerProgram(options);
        var model = LlamaServerPaths.FromEngineFolder(options.ModelPath);
        if (!File.Exists(program) || !File.Exists(model))
        {
            Publish(new(LlamaServerState.Missing));
            LogFilesMissing(role.Label, program, File.Exists(program), model, File.Exists(model));
            return;
        }

        string pidFile;
        try
        {
            pidFile = dataFolder.File(role.PidFileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Fail(new("The engine could not create its data folder. See the engine log.", $"The engine could not create {dataFolder.Path}: {exception.Message}"));
            return;
        }

        // The instance lock makes sure that no other engine owns these servers.
        // The legacy file belongs to the main model. Both models use the same program, so only one of them may read it.
        if (role.IsMain)
        {
            var legacyPidFile = LlamaServerPidFile.LegacyPidFilePath(program);
            LlamaServerPidFile.StopLeftoverServer(legacyPidFile, program, logger);
            LlamaServerPidFile.TryDeletePidFile(legacyPidFile, logger);
        }

        LlamaServerPidFile.StopLeftoverServer(pidFile, program, logger);

        var crashes = new CrashBudget(timeProvider);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                RunResult result;
                try
                {
                    result = await RunOnceAsync(program, pidFile, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A failure here must not stop the host, because the other model can keep working.
                    LogRunFailed(exception);
                    Fail(new("The llama-server run failed unexpectedly. See the engine log.", exception.ToString()));
                    return;
                }

                // Ctrl+C in a terminal also stops llama-server. That is a shutdown, not a failure.
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                if (!result.BecameReady)
                {
                    Fail(result.Failure);
                    return;
                }

                if (crashes.RecordCrash())
                {
                    Fail(new(
                        $"llama-server stopped {CrashBudget.MaxCrashes} times within {CrashBudget.Window.TotalMinutes:0} minutes. See the engine log. Restart the engine to try again.",
                        $"llama-server stopped {CrashBudget.MaxCrashes} times within {CrashBudget.Window.TotalMinutes:0} minutes. The last stop: {result.Failure.Detail}"));
                    return;
                }

                restarts?.Add(1, new KeyValuePair<string, object?>("model", role.Label), new KeyValuePair<string, object?>("cause", result.Hung ? "hang" : "crash"));
                Publish(new(LlamaServerState.Restarting));
                LogRestarting(RestartDelay);
                await Task.Delay(RestartDelay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown: the server has already stopped.
        }
        finally
        {
            if (snapshot.State != LlamaServerState.Failed)
            {
                Publish(new(LlamaServerState.Stopped));
            }
        }
    }

    /// <summary>Chooses a port the system reports as free. Another program could take it before the server binds it. The key check after the load finds this case.</summary>
    /// <returns>The port number.</returns>
    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Replaces the state, and gives it to the request gate.</summary>
    /// <param name="next">The new state.</param>
    private void Publish(LlamaServerSnapshot next)
    {
        snapshot = next;
        gate.Update(next);
    }

    /// <summary>Stops the local model for the rest of this engine run, and keeps the cause for local requests.</summary>
    /// <param name="failure">The cause.</param>
    private void Fail(LlamaFailure failure)
    {
        Publish(new(LlamaServerState.Failed, Failure: failure));
        LogFailed(role.Label, failure.Detail);
    }

    /// <summary>Runs one llama-server process from start to exit.</summary>
    /// <param name="program">The llama-server path.</param>
    /// <param name="pidFile">The PID file path.</param>
    /// <param name="stoppingToken">Signals engine shutdown.</param>
    /// <returns>Whether the model loaded before the process ended, and the cause of the end.</returns>
    private async Task<RunResult> RunOnceAsync(string program, string pidFile, CancellationToken stoppingToken)
    {
        Publish(new(LlamaServerState.Starting));
        if (LlamaConfigFilePolicy.FindConfigFile(program) is { } configFile)
        {
            return new(false, LlamaConfigFilePolicy.Failure(configFile));
        }

        var port = FindFreePort();
        var apiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        using var process = new LlamaServerProcess(program, LlamaServerArguments.Build(options, port), apiKey, logger);
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            LogStartFailed(exception, program);
            return new(false, new("The engine could not start llama-server. See the engine log.", $"The engine could not start {program}: {exception.Message}"));
        }

        try
        {
            // A process that has already exited has no start time. Then the PID file is not needed.
            if (process.TryFormatPidRecord() is { } record)
            {
                LlamaServerPidFile.WritePidFile(pidFile, record, logger);
            }

            LogStarted(process.Id, port);
            var baseUrl = new Uri($"http://{IPAddress.Loopback}:{port}/");
            if (await WaitUntilLoadedAsync(process, baseUrl, apiKey, stoppingToken).ConfigureAwait(false) is { } loadFailure)
            {
                return new(false, loadFailure);
            }

            process.StopKeepingErrors();
            var endpoint = new LlamaServerEndpoint(new Uri(baseUrl, "v1"), apiKey);
            Publish(new(LlamaServerState.Ready, endpoint));
            LogReady(role.Label);

            var (cause, hung) = await WatchUntilStoppedAsync(process, baseUrl, endpoint, stoppingToken).ConfigureAwait(false);

            // New requests must get the right message at once, not a connection error while the old process stops.
            Publish(new(LlamaServerState.Restarting));
            return new(true, cause, hung);
        }
        finally
        {
            process.StopKeepingErrors();
            await process.StopAsync().ConfigureAwait(false);
            LlamaServerPidFile.TryDeletePidFile(pidFile, logger);
        }
    }

    /// <summary>Waits until the server reports that the model has loaded, and then checks that the server is the engine's own.</summary>
    /// <param name="process">The server process.</param>
    /// <param name="baseUrl">The server's address.</param>
    /// <param name="apiKey">The key of this run.</param>
    /// <param name="stoppingToken">Signals engine shutdown.</param>
    /// <returns><see langword="null"/> when the server is ready, or the cause when it exited or ran out of time.</returns>
    private async Task<LlamaFailure?> WaitUntilLoadedAsync(LlamaServerProcess process, Uri baseUrl, string apiKey, CancellationToken stoppingToken)
    {
        var startedAt = timeProvider.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(options.StartupTimeoutSeconds);
        while (timeProvider.GetElapsedTime(startedAt) < timeout)
        {
            if (process.HasExited)
            {
                await process.DrainOutputAsync(stoppingToken).ConfigureAwait(false);

                // The exit code helps when the last error line is from an earlier problem that llama-server survived.
                var line = process.LastStartupError is { } error ? $" Its last error line: {error}" : string.Empty;
                return new(
                    $"llama-server stopped with exit code {process.ExitCode} while the model loaded. See the engine log.",
                    $"llama-server stopped with exit code {process.ExitCode} while the model loaded.{line}");
            }

            if (await probe.IsHealthyAsync(baseUrl, stoppingToken).ConfigureAwait(false))
            {
                return await probe.AcceptsKeyAsync(baseUrl, apiKey, stoppingToken).ConfigureAwait(false)
                    ? null
                    : new(
                        "Another program answered on the port of llama-server. Restart the engine.",
                        $"The server on {baseUrl} answered the health check, but refused the key of this run. Another program can use the port.");
            }

            await Task.Delay(HealthPollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }

        var message = $"The model did not load within {options.StartupTimeoutSeconds} seconds. Increase {role.SectionName}:StartupTimeoutSeconds, or use a smaller model.";
        return new(message, message);
    }

    /// <summary>Returns when a ready server has exited, or when the engine must replace it because it hangs.</summary>
    /// <remarks>
    /// A process that stops responding never exits by itself. Without these checks, local requests wait forever. A busy server
    /// can also answer health checks slowly, so failed checks stop the server only while no request runs, or after a long time
    /// with no health and no data. See ADR 0009.
    /// </remarks>
    /// <param name="process">The server process.</param>
    /// <param name="baseUrl">The server's address.</param>
    /// <param name="endpoint">The address for requests.</param>
    /// <param name="stoppingToken">Signals engine shutdown.</param>
    /// <returns>The cause, when the engine must replace the server, and whether the server hung.</returns>
    private async Task<(LlamaFailure Cause, bool Hung)> WatchUntilStoppedAsync(LlamaServerProcess process, Uri baseUrl, LlamaServerEndpoint endpoint, CancellationToken stoppingToken)
    {
        var exited = process.WaitForExitAsync(stoppingToken);
        var watch = new HangWatch(timeProvider, gate, TimeSpan.FromSeconds(options.UnresponsiveTimeoutSeconds));
        while (true)
        {
            var nextCheck = Task.Delay(TimeSpan.FromSeconds(options.HealthCheckIntervalSeconds), timeProvider, stoppingToken);
            if (await Task.WhenAny(exited, nextCheck).ConfigureAwait(false) == exited)
            {
                await exited.ConfigureAwait(false);
                LogExited(process.ExitCode);
                var stopped = $"llama-server stopped with exit code {process.ExitCode}.";
                return (new(stopped, stopped), false);
            }

            await nextCheck.ConfigureAwait(false);
            var healthy = await probe.IsHealthyAsync(baseUrl, stoppingToken).ConfigureAwait(false);
            switch (watch.Record(healthy))
            {
                case HangDecision.Serve when snapshot.State != LlamaServerState.Ready:
                    LogRecovered();
                    Publish(new(LlamaServerState.Ready, endpoint));
                    break;
                case HangDecision.HoldNewRequests when snapshot.State != LlamaServerState.Unresponsive:
                    LogUnresponsive(FailedChecksLimit);
                    Publish(new(LlamaServerState.Unresponsive, endpoint));
                    break;
                case HangDecision.Restart:
                    LogHung(watch.FailedChecks);
                    return (new("llama-server stopped responding. The engine starts it again.", watch.Cause), true);
            }
        }
    }

    /// <summary>Logs that a required file is missing.</summary>
    /// <param name="provider">The provider name of the model.</param>
    /// <param name="program">The llama-server path.</param>
    /// <param name="programFound">Whether llama-server exists.</param>
    /// <param name="model">The model path.</param>
    /// <param name="modelFound">Whether the model exists.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "The provider {Provider} is off. Server {Program} found: {ProgramFound}. Model {Model} found: {ModelFound}. Download the model from the GitHub release, then restart.")]
    private partial void LogFilesMissing(string provider, string program, bool programFound, string model, bool modelFound);

    /// <summary>Logs a failed process start.</summary>
    /// <param name="exception">The cause.</param>
    /// <param name="program">The llama-server path.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "The engine could not start {Program}. On macOS, you may have to remove the quarantine flag from a downloaded file.")]
    private partial void LogStartFailed(Exception exception, string program);

    /// <summary>Logs a started process.</summary>
    /// <param name="pid">The process ID.</param>
    /// <param name="port">The port.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "llama-server started as process {Pid} on port {Port}. The model is loading.")]
    private partial void LogStarted(int pid, int port);

    /// <summary>Logs that the model has loaded.</summary>
    /// <param name="provider">The provider name of the model.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "The model of the provider {Provider} is ready.")]
    private partial void LogReady(string provider);

    /// <summary>Logs a server that answers its health checks again.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "llama-server answers its health checks again. New local requests can start.")]
    private partial void LogRecovered();

    /// <summary>Logs a busy server that does not answer its health checks.</summary>
    /// <param name="failedChecks">The number of failed checks in a row.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "llama-server failed {FailedChecks} health checks in a row while requests ran. New local requests get 503 until it answers again. Requests in progress continue.")]
    private partial void LogUnresponsive(int failedChecks);

    /// <summary>Logs an unexpected exit of a ready server.</summary>
    /// <param name="exitCode">The exit code.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "llama-server stopped unexpectedly with exit code {ExitCode}.")]
    private partial void LogExited(int exitCode);

    /// <summary>Logs that the local model failed and will not restart.</summary>
    /// <param name="provider">The provider name of the model.</param>
    /// <param name="cause">The full cause.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "The model of the provider {Provider} failed, and the engine will not start llama-server again until the engine restarts. Cause: {Cause}")]
    private partial void LogFailed(string provider, string cause);

    /// <summary>Logs a server that the engine replaces because it hangs.</summary>
    /// <param name="failedChecks">The number of failed health checks in a row.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "llama-server failed {FailedChecks} health checks in a row, and the engine stops it.")]
    private partial void LogHung(int failedChecks);

    /// <summary>Logs an unexpected failure while running the server.</summary>
    /// <param name="exception">The cause.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "The llama-server run failed unexpectedly.")]
    private partial void LogRunFailed(Exception exception);

    /// <summary>Logs a scheduled restart.</summary>
    /// <param name="delay">The wait before the restart.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "The engine will restart llama-server after {Delay}.")]
    private partial void LogRestarting(TimeSpan delay);

    /// <summary>Gives the tag of a state. These values are stable, so they never come from enum names.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The tag value.</returns>
    private static string StateTag(LlamaServerState state) => state switch
    {
        LlamaServerState.Starting => "starting",
        LlamaServerState.Ready => "ready",
        LlamaServerState.Unresponsive => "unresponsive",
        LlamaServerState.Restarting => "restarting",
        LlamaServerState.Missing => "missing",
        LlamaServerState.Stopped => "stopping",
        LlamaServerState.Failed => "failed",
    };

    /// <inheritdoc/>
    public override void Dispose()
    {
        meter?.Dispose();
        base.Dispose();
    }

    /// <summary>The end of one llama-server run.</summary>
    /// <param name="BecameReady">Whether the model loaded before the run ended.</param>
    /// <param name="Failure">Why the run ended.</param>
    /// <param name="Hung">Whether the engine stopped the server because it hung.</param>
    private sealed record RunResult(bool BecameReady, LlamaFailure Failure, bool Hung = false);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
/// <see cref="Endpoint"/> and <see cref="State"/> are safe to read from any thread.
/// </remarks>
internal sealed partial class LlamaServerSupervisor : BackgroundService
{
    /// <summary>The first delay before a restart.</summary>
    private static readonly TimeSpan InitialRestartDelay = TimeSpan.FromSeconds(1);

    /// <summary>The longest delay before a restart.</summary>
    private static readonly TimeSpan MaximumRestartDelay = TimeSpan.FromSeconds(60);

    /// <summary>The wait between checks while the model loads.</summary>
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest wait for one health check, so that a server that does not respond cannot block the startup loop.</summary>
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How many health checks in a row must fail before a running server counts as not responding.</summary>
    private const int HungAfterFailedChecks = 3;

    /// <summary>The local model settings.</summary>
    private readonly LlamaCppOptions options;

    /// <summary>Creates the client for health checks.</summary>
    private readonly IHttpClientFactory httpClientFactory;

    /// <summary>The clock for delays, which tests can replace.</summary>
    private readonly TimeProvider timeProvider;

    /// <summary>The log for lifecycle events.</summary>
    private readonly ILogger<LlamaServerSupervisor> logger;

    /// <summary>The address of the ready server, or <see langword="null"/> when the server cannot serve requests.</summary>
    private volatile LlamaServerEndpoint? endpoint;

    /// <summary>The current stage, as a volatile enum value that any thread can read.</summary>
    private volatile LlamaServerState state = LlamaServerState.Starting;

    /// <summary>Initializes a new instance of the <see cref="LlamaServerSupervisor"/> class.</summary>
    /// <param name="options">The local model settings.</param>
    /// <param name="httpClientFactory">Creates the client for health checks.</param>
    /// <param name="timeProvider">The clock for delays.</param>
    /// <param name="logger">The log for lifecycle events.</param>
    public LlamaServerSupervisor(
        IOptions<LlamaCppOptions> options,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger<LlamaServerSupervisor> logger)
    {
        this.options = options.Value;
        this.httpClientFactory = httpClientFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>Gets the address of the ready server, or <see langword="null"/> when the server cannot serve requests.</summary>
    public LlamaServerEndpoint? Endpoint => endpoint;

    /// <summary>Gets the current stage.</summary>
    public LlamaServerState State => state;

    /// <summary>Builds the llama-server arguments. The API key is not here, because arguments are visible to every user on the machine.</summary>
    /// <param name="options">The local model settings.</param>
    /// <param name="port">The port to listen on.</param>
    /// <returns>The arguments in order.</returns>
    internal static IReadOnlyList<string> BuildArguments(LlamaCppOptions options, int port) =>
    [
        "--model", LlamaServerPaths.FromEngineFolder(options.ModelPath),
        "--host", IPAddress.Loopback.ToString(),
        "--port", port.ToString(CultureInfo.InvariantCulture),
        "--ctx-size", options.ContextSize.ToString(CultureInfo.InvariantCulture),
        "--gpu-layers", options.GpuLayers,
        "--parallel", options.Parallel.ToString(CultureInfo.InvariantCulture),
        "--no-webui",
        .. options.ExtraArguments,
    ];

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var program = LlamaServerPaths.ServerProgram(options);
        var model = LlamaServerPaths.FromEngineFolder(options.ModelPath);
        if (!File.Exists(program) || !File.Exists(model))
        {
            state = LlamaServerState.Missing;
            LogFilesMissing(program, File.Exists(program), model, File.Exists(model));
            return;
        }

        var pidFile = LlamaServerPidFile.PidFilePath(program);
        LlamaServerPidFile.StopLeftoverServer(pidFile, program, logger);

        var delay = InitialRestartDelay;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var becameReady = false;
                try
                {
                    becameReady = await RunOnceAsync(program, pidFile, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A failure here must not stop the host, because cloud providers can keep working without the local model.
                    LogRunFailed(exception);
                }

                endpoint = null;
                state = LlamaServerState.Restarting;
                delay = becameReady ? InitialRestartDelay : Min(delay + delay, MaximumRestartDelay);
                LogRestarting(delay);
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown: RunOnceAsync has already stopped the server.
        }
        finally
        {
            endpoint = null;
            state = LlamaServerState.Stopped;
        }
    }

    /// <summary>Gives the smaller of two delays.</summary>
    /// <param name="first">The first delay.</param>
    /// <param name="second">The second delay.</param>
    /// <returns>The smaller delay.</returns>
    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    /// <summary>Chooses a port the system reports as free. Another program could take it before the server binds it, which then causes a normal restart.</summary>
    /// <returns>The port number.</returns>
    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Stops the server's whole process tree and waits until it has exited.</summary>
    /// <param name="process">The server process.</param>
    /// <returns>A task that completes when the process has exited.</returns>
    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or AggregateException)
        {
            // The process exited between the check and the kill, or the engine could not stop it. This code cannot correct either problem.
        }
    }

    /// <summary>Runs one llama-server process from start to exit.</summary>
    /// <param name="program">The llama-server path.</param>
    /// <param name="pidFile">The PID file path.</param>
    /// <param name="stoppingToken">Signals engine shutdown.</param>
    /// <returns><see langword="true"/> when the model loaded before the process ended.</returns>
    private async Task<bool> RunOnceAsync(string program, string pidFile, CancellationToken stoppingToken)
    {
        state = LlamaServerState.Starting;
        var port = FindFreePort();
        var apiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        using var process = CreateProcess(program, port, apiKey);
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            LogStartFailed(exception, program);
            return false;
        }

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            LlamaServerPidFile.WritePidFile(pidFile, LlamaServerPidFile.FormatPidRecord(process), logger);
            LogStarted(process.Id, port);

            var baseUrl = new Uri($"http://{IPAddress.Loopback}:{port}/");
            if (!await WaitUntilHealthyAsync(process, baseUrl, stoppingToken).ConfigureAwait(false))
            {
                return false;
            }

            endpoint = new LlamaServerEndpoint(new Uri(baseUrl, "v1"), apiKey);
            state = LlamaServerState.Ready;
            LogReady();

            await WatchUntilStoppedAsync(process, baseUrl, stoppingToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            endpoint = null;
            await StopProcessAsync(process).ConfigureAwait(false);
            LlamaServerPidFile.TryDeletePidFile(pidFile, logger);
        }
    }

    /// <summary>Prepares the process. The engine sends the output to the log, because a full pipe blocks the server.</summary>
    /// <param name="program">The llama-server path.</param>
    /// <param name="port">The port to listen on.</param>
    /// <param name="apiKey">The key for this run, passed through the environment so that other users cannot see it.</param>
    /// <returns>The configured process, not yet started.</returns>
    private Process CreateProcess(string program, int port, string apiKey)
    {
        var folder = Path.GetDirectoryName(program)!;
        var startInfo = new ProcessStartInfo(program)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in BuildArguments(options, port))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // A child process inherits the environment, and llama-server has no need for the engine's own secrets.
        foreach (var name in startInfo.Environment.Keys.Where(IsEngineSecret).ToList())
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment["LLAMA_API_KEY"] = apiKey;
        if (OperatingSystem.IsLinux())
        {
            // Existing entries stay, because GPU drivers can depend on them.
            var existing = startInfo.Environment.TryGetValue("LD_LIBRARY_PATH", out var value) ? value : null;
            startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? folder : $"{folder}:{existing}";
        }

        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, line) => LogOutputIfPresent(line.Data);
        process.ErrorDataReceived += (_, line) => LogOutputIfPresent(line.Data);
        return process;
    }

    /// <summary>Tells whether an environment variable holds settings for the engine's cloud providers.</summary>
    /// <param name="name">The variable's name.</param>
    /// <returns><see langword="true"/> when the variable must not reach llama-server.</returns>
    private static bool IsEngineSecret(string name) =>
        name.StartsWith("Providers__", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Providers:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Logs one line of server output.</summary>
    /// <param name="line">The line, or <see langword="null"/> when the stream ended.</param>
    private void LogOutputIfPresent(string? line)
    {
        if (line is not null)
        {
            LogServerOutput(line);
        }
    }

    /// <summary>Waits until the server reports that the model has loaded.</summary>
    /// <param name="process">The server process.</param>
    /// <param name="baseUrl">The server's address.</param>
    /// <param name="stoppingToken">Signals engine shutdown.</param>
    /// <returns><see langword="true"/> when the server is ready, or <see langword="false"/> when it exited or ran out of time.</returns>
    private async Task<bool> WaitUntilHealthyAsync(Process process, Uri baseUrl, CancellationToken stoppingToken)
    {
        using var client = httpClientFactory.CreateClient(DependencyInjection.LocalHttpClientName);
        var healthUrl = new Uri(baseUrl, "health");
        var startedAt = timeProvider.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(options.StartupTimeoutSeconds);

        while (timeProvider.GetElapsedTime(startedAt) < timeout)
        {
            if (process.HasExited)
            {
                LogExitedWhileLoading(process.ExitCode);
                return false;
            }

            if (await IsHealthyAsync(client, healthUrl, stoppingToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(HealthPollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }

        LogLoadTimedOut(timeout);
        return false;
    }

    /// <summary>Checks once whether the server answers its health route.</summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="healthUrl">The health route.</param>
    /// <param name="stoppingToken">Signals engine shutdown.</param>
    /// <returns><see langword="true"/> when the server reports that it is ready.</returns>
    private static async Task<bool> IsHealthyAsync(HttpClient client, Uri healthUrl, CancellationToken stoppingToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        attempt.CancelAfter(HealthRequestTimeout);
        try
        {
            using var response = await client.GetAsync(healthUrl, attempt.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // The port is not open.
            return false;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // This check timed out.
            return false;
        }
    }

    /// <summary>Returns when a ready server has exited or stopped responding. A process that stops responding never exits by itself. Without these checks, local requests wait forever.</summary>
    /// <param name="process">The server process.</param>
    /// <param name="baseUrl">The server's address.</param>
    /// <param name="stoppingToken">Signals engine shutdown.</param>
    /// <returns>A task that completes when the engine must replace the server.</returns>
    private async Task WatchUntilStoppedAsync(Process process, Uri baseUrl, CancellationToken stoppingToken)
    {
        using var client = httpClientFactory.CreateClient(DependencyInjection.LocalHttpClientName);
        var healthUrl = new Uri(baseUrl, "health");
        var exited = process.WaitForExitAsync(stoppingToken);
        var failures = 0;

        while (failures < HungAfterFailedChecks)
        {
            var nextCheck = Task.Delay(TimeSpan.FromSeconds(options.HealthCheckIntervalSeconds), timeProvider, stoppingToken);
            if (await Task.WhenAny(exited, nextCheck).ConfigureAwait(false) == exited)
            {
                await exited.ConfigureAwait(false);
                LogExited(process.ExitCode);
                return;
            }

            await nextCheck.ConfigureAwait(false);
            failures = await IsHealthyAsync(client, healthUrl, stoppingToken).ConfigureAwait(false) ? 0 : failures + 1;
        }

        LogHung(HungAfterFailedChecks);
    }

    /// <summary>Logs that a required file is missing.</summary>
    /// <param name="program">The llama-server path.</param>
    /// <param name="programFound">Whether llama-server exists.</param>
    /// <param name="model">The model path.</param>
    /// <param name="modelFound">Whether the model exists.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "The local provider is off. Server {Program} found: {ProgramFound}. Model {Model} found: {ModelFound}. Download the model from the GitHub release, then restart.")]
    private partial void LogFilesMissing(string program, bool programFound, string model, bool modelFound);

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
    [LoggerMessage(Level = LogLevel.Information, Message = "The local model is ready.")]
    private partial void LogReady();

    /// <summary>Logs an unexpected exit of a ready server.</summary>
    /// <param name="exitCode">The exit code.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "llama-server stopped unexpectedly with exit code {ExitCode}.")]
    private partial void LogExited(int exitCode);

    /// <summary>Logs an exit during model loading.</summary>
    /// <param name="exitCode">The exit code.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "llama-server stopped with exit code {ExitCode} during the model load. To see its output, set the log level of this class to Debug.")]
    private partial void LogExitedWhileLoading(int exitCode);

    /// <summary>Logs a server that stopped responding while still running.</summary>
    /// <param name="failedChecks">The number of failed health checks in a row.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "llama-server failed {FailedChecks} health checks in a row, so the engine will replace it.")]
    private partial void LogHung(int failedChecks);

    /// <summary>Logs an unexpected failure while running the server.</summary>
    /// <param name="exception">The cause.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "The llama-server run failed unexpectedly.")]
    private partial void LogRunFailed(Exception exception);

    /// <summary>Logs a load that took too long.</summary>
    /// <param name="timeout">The limit.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "The local model did not load within {Timeout}.")]
    private partial void LogLoadTimedOut(TimeSpan timeout);

    /// <summary>Logs a scheduled restart.</summary>
    /// <param name="delay">The wait before the restart.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "The engine will restart llama-server after {Delay}.")]
    private partial void LogRestarting(TimeSpan delay);

    /// <summary>Logs one line of server output. This is Debug level, because a verbose server could print prompts.</summary>
    /// <param name="line">The line.</param>
    [LoggerMessage(Level = LogLevel.Debug, Message = "llama-server: {Line}")]
    private partial void LogServerOutput(string line);
}

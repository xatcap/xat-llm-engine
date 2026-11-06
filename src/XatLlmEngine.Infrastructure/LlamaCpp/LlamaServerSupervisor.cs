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
    /// <summary>The delay before a restart after a crash.</summary>
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);

    /// <summary>The period in which <see cref="MaxCrashes"/> crashes stop the restarts.</summary>
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);

    /// <summary>How many crashes of a ready server in <see cref="CrashWindow"/> stop the restarts.</summary>
    private const int MaxCrashes = 3;

    /// <summary>The wait between checks while the model loads.</summary>
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest wait for one health check, so that a server that does not respond cannot block the startup loop.</summary>
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The prefixes of the llama.cpp variables. See <see cref="IsLlamaSetting"/>.</summary>
    private static readonly string[] LlamaNameSpaces = ["LLAMA_", "LLAMACPP_", "GGML_", "MTMD_", "AIP_", "HF_", "LLGUIDANCE_"];

    /// <summary>The llama.cpp variables outside the name spaces. See <see cref="IsLlamaSetting"/>.</summary>
    private static readonly string[] LlamaNames = ["MODEL_ENDPOINT", "HTTP_PROXY", "HTTPS_PROXY"];

    /// <summary>The longest wait for the rest of the output after llama-server exits.</summary>
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The longest error line that the engine keeps.</summary>
    private const int MaxErrorLineLength = 500;

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

    /// <summary>Why the local model failed, or <see langword="null"/> when it did not fail.</summary>
    private volatile string? failureReason;

    /// <summary>The last error line that llama-server wrote before it became ready.</summary>
    private volatile string? lastStartupError;

    /// <summary>Whether the output handlers keep error lines. This is true only before the server is ready, so the engine never keeps a line that can hold a prompt.</summary>
    private volatile bool keepStartupErrors;

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

    /// <summary>Gets why the local model failed, when <see cref="State"/> is <see cref="LlamaServerState.Failed"/>.</summary>
    public string? FailureReason => failureReason;

    /// <summary>Builds the llama-server arguments. The API key is not here, because arguments are visible to every user on the machine.</summary>
    /// <remarks>The context size, GPU layers and parallel slots come from the settings, unless <see cref="LlamaCppOptions.ExtraArguments"/> sets them. Thus llama-server never gets the same flag two times.</remarks>
    /// <param name="options">The local model settings.</param>
    /// <param name="port">The port to listen on.</param>
    /// <returns>The arguments in order.</returns>
    internal static IReadOnlyList<string> BuildArguments(LlamaCppOptions options, int port) =>
    [
        "--model", LlamaServerPaths.FromEngineFolder(options.ModelPath),
        "--host", IPAddress.Loopback.ToString(),
        "--port", port.ToString(CultureInfo.InvariantCulture),
        .. options.HasExtraFlag("-c", "--ctx-size") ? [] : new[] { "--ctx-size", options.ContextSize.ToString(CultureInfo.InvariantCulture) },
        .. options.HasExtraFlag("-ngl", "--gpu-layers", "--n-gpu-layers") ? [] : new[] { "--gpu-layers", options.GpuLayers },
        .. options.HasExtraFlag("-np", "--parallel") ? [] : new[] { "--parallel", options.Parallel.ToString(CultureInfo.InvariantCulture) },
        "--no-webui",
        .. options.ExtraArguments,
    ];

    /// <summary>Stops the engine at startup when a llama.cpp config file exists, then starts the server.</summary>
    /// <remarks>
    /// llama-server always reads these files, and no flag or variable turns this off. A file can turn on options that
    /// <see cref="LlamaCppOptions.ExtraArguments"/> does not allow, such as a log file. The engine cannot replace all these
    /// values safely from the command line, so it does not start.
    /// </remarks>
    /// <param name="cancellationToken">Signals that the start must stop.</param>
    /// <returns>A task that completes when the server has started.</returns>
    /// <exception cref="InvalidOperationException">A llama.cpp config file exists.</exception>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Without the program or the model, llama-server never starts, so a config file has no effect. ExecuteAsync reports the missing files.
        var program = LlamaServerPaths.ServerProgram(options);
        if (File.Exists(program) && File.Exists(LlamaServerPaths.FromEngineFolder(options.ModelPath)))
        {
            ThrowIfConfigFileExists(program);
        }

        return base.StartAsync(cancellationToken);
    }

    /// <summary>Stops a llama-server start when a llama.cpp config file exists.</summary>
    /// <remarks>
    /// llama-server resolves a relative folder from its own working folder, so this method does the same.
    /// <see cref="RunOnceAsync"/> also calls this method, because a file can appear while the engine runs, before a restart.
    /// In that case, the local model fails and does not restart.
    /// </remarks>
    /// <param name="program">The llama-server path. Its folder is the working folder of llama-server.</param>
    /// <exception cref="InvalidOperationException">A llama.cpp config file exists.</exception>
    private static void ThrowIfConfigFileExists(string program)
    {
        var folder = Path.GetDirectoryName(program)!;
        var configFile = LlamaServerPaths.ConfigFiles(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable)
            .Select(path => Path.GetFullPath(path, folder))
            .FirstOrDefault(File.Exists);
        if (configFile is not null)
        {
            throw new InvalidOperationException(
                $"The llama.cpp config file '{configFile}' can change llama-server settings, and the engine cannot turn it off. Remove or rename the file. Then start the engine again.");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A failed start never causes a retry, because a wrong setting does not correct itself. Local requests then get the cause.
    /// A crash of a ready server causes a restart, but <see cref="MaxCrashes"/> crashes in <see cref="CrashWindow"/> stop the restarts.
    /// </remarks>
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

        var crashTimes = new Queue<DateTimeOffset>();
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
                    // A failure here must not stop the host, because cloud providers can keep working without the local model.
                    LogRunFailed(exception);
                    Fail($"The llama-server run failed unexpectedly: {exception.Message}");
                    return;
                }

                // Ctrl+C in a terminal also stops llama-server. That is a shutdown, not a failure.
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                if (!result.BecameReady)
                {
                    Fail(result.Cause);
                    return;
                }

                var now = timeProvider.GetUtcNow();
                crashTimes.Enqueue(now);
                while (crashTimes.Peek() <= now - CrashWindow)
                {
                    crashTimes.Dequeue();
                }

                if (crashTimes.Count >= MaxCrashes)
                {
                    Fail($"llama-server stopped {MaxCrashes} times within {CrashWindow.TotalMinutes:0} minutes. The last stop: {result.Cause}");
                    return;
                }

                endpoint = null;
                state = LlamaServerState.Restarting;
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
            endpoint = null;
            if (state != LlamaServerState.Failed)
            {
                state = LlamaServerState.Stopped;
            }
        }
    }

    /// <summary>Stops the local model for the rest of this engine run, and keeps the cause for local requests.</summary>
    /// <param name="reason">The cause, as text that a person can read.</param>
    private void Fail(string reason)
    {
        endpoint = null;

        // The reason is set first, so that a thread that sees the Failed state also sees the reason.
        failureReason = reason;
        state = LlamaServerState.Failed;
        LogFailed(reason);
    }

    /// <summary>Chooses a port the system reports as free. Another program could take it before the server binds it. Then the start fails, and the local model fails until the engine restarts. This is very rare.</summary>
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
    /// <returns>Whether the model loaded before the process ended, and the cause of the end.</returns>
    private async Task<RunResult> RunOnceAsync(string program, string pidFile, CancellationToken stoppingToken)
    {
        state = LlamaServerState.Starting;
        try
        {
            ThrowIfConfigFileExists(program);
        }
        catch (InvalidOperationException exception)
        {
            return new(false, exception.Message);
        }

        var port = FindFreePort();
        var apiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        lastStartupError = null;
        keepStartupErrors = true;
        using var process = CreateProcess(program, port, apiKey);
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            LogStartFailed(exception, program);
            return new(false, $"The engine could not start llama-server: {exception.Message}");
        }

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            LlamaServerPidFile.WritePidFile(pidFile, LlamaServerPidFile.FormatPidRecord(process), logger);
            LogStarted(process.Id, port);

            var baseUrl = new Uri($"http://{IPAddress.Loopback}:{port}/");
            var loadProblem = await WaitUntilHealthyAsync(process, baseUrl, stoppingToken).ConfigureAwait(false);
            if (loadProblem is not null)
            {
                return new(false, loadProblem);
            }

            keepStartupErrors = false;
            endpoint = new LlamaServerEndpoint(new Uri(baseUrl, "v1"), apiKey);
            state = LlamaServerState.Ready;
            LogReady();

            return new(true, await WatchUntilStoppedAsync(process, baseUrl, stoppingToken).ConfigureAwait(false));
        }
        finally
        {
            keepStartupErrors = false;
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

        // A child process inherits the environment. llama-server has no need for the engine's own secrets.
        // llama-server also reads its own variables, and these can turn on options that ExtraArguments does not allow.
        foreach (var name in startInfo.Environment.Keys.Where(name => IsEngineSecret(name) || IsLlamaSetting(name)).ToList())
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
        process.OutputDataReceived += (_, line) => HandleOutput(line.Data);
        process.ErrorDataReceived += (_, line) => HandleOutput(line.Data);
        return process;
    }

    /// <summary>Tells whether an environment variable holds settings for the engine's cloud providers.</summary>
    /// <param name="name">The variable's name.</param>
    /// <returns><see langword="true"/> when the variable must not reach llama-server.</returns>
    private static bool IsEngineSecret(string name) =>
        name.StartsWith("Providers__", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Providers:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tells whether an environment variable holds a llama.cpp setting.</summary>
    /// <remarks>
    /// A scan of all the llama.cpp b12110 source that the packages contain found these variables:
    /// <list type="bullet">
    /// <item>names in the llama.cpp name spaces <c>LLAMA_</c>, <c>LLAMACPP_</c>, <c>GGML_</c>, <c>MTMD_</c>, <c>AIP_</c>, <c>HF_</c> and <c>LLGUIDANCE_</c>;</item>
    /// <item><c>MODEL_ENDPOINT</c>, <c>HTTP_PROXY</c> and <c>HTTPS_PROXY</c>, which only downloads use.</item>
    /// </list>
    /// Some of them change the port, load a library, write files or print internal data. Later versions use the same name spaces, so this rule also removes their new names.
    /// The rule keeps the variables of the operating system and the GPU drivers, such as <c>PATH</c>, <c>HOME</c>, <c>XDG_*</c>, <c>TEMP</c> and <c>VK_*</c>. llama-server and the drivers need them.
    /// The engine sets <c>LLAMA_API_KEY</c> itself, after it removes these variables.
    /// </remarks>
    /// <param name="name">The variable's name.</param>
    /// <returns><see langword="true"/> when the variable must not reach llama-server.</returns>
    private static bool IsLlamaSetting(string name) =>
        LlamaNameSpaces.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || LlamaNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Handles one line of server output. The line goes to the log at Debug level. Before the server is ready, the engine also keeps the last error line.</summary>
    /// <param name="line">The line, or <see langword="null"/> when the stream ended.</param>
    private void HandleOutput(string? line)
    {
        if (line is null)
        {
            return;
        }

        LogServerOutput(line);
        if (keepStartupErrors
            && (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase)))
        {
            // A limit on the length keeps the log and the error message short.
            var trimmed = line.Trim();
            lastStartupError = trimmed.Length <= MaxErrorLineLength ? trimmed : trimmed[..MaxErrorLineLength];
        }
    }

    /// <summary>Waits until the server reports that the model has loaded.</summary>
    /// <param name="process">The server process.</param>
    /// <param name="baseUrl">The server's address.</param>
    /// <param name="stoppingToken">Signals engine shutdown.</param>
    /// <returns><see langword="null"/> when the server is ready, or the cause when it exited or ran out of time.</returns>
    private async Task<string?> WaitUntilHealthyAsync(Process process, Uri baseUrl, CancellationToken stoppingToken)
    {
        using var client = httpClientFactory.CreateClient(DependencyInjection.LocalHttpClientName);
        var healthUrl = new Uri(baseUrl, "health");
        var startedAt = timeProvider.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(options.StartupTimeoutSeconds);

        while (timeProvider.GetElapsedTime(startedAt) < timeout)
        {
            if (process.HasExited)
            {
                // This wait also reads the rest of the output, so that the last error line is complete.
                // A child process of llama-server can keep the output open, so the wait has a limit.
                using (var drain = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    drain.CancelAfter(OutputDrainTimeout);
                    try
                    {
                        await process.WaitForExitAsync(drain.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // The output did not end in time. The engine uses the lines that it has.
                    }
                }

                // The exit code helps when the last error line is from an earlier problem that llama-server survived.
                return lastStartupError is { } line
                    ? $"{line.TrimEnd('.')} (exit code {process.ExitCode})."
                    : $"llama-server stopped with exit code {process.ExitCode} while the model loaded.";
            }

            if (await IsHealthyAsync(client, healthUrl, stoppingToken).ConfigureAwait(false))
            {
                return null;
            }

            await Task.Delay(HealthPollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }

        return $"The model did not load within {options.StartupTimeoutSeconds} seconds. Increase LocalModel:StartupTimeoutSeconds, or use a smaller model.";
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
    /// <returns>The cause, when the engine must replace the server.</returns>
    private async Task<string> WatchUntilStoppedAsync(Process process, Uri baseUrl, CancellationToken stoppingToken)
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
                return $"llama-server stopped with exit code {process.ExitCode}.";
            }

            await nextCheck.ConfigureAwait(false);
            failures = await IsHealthyAsync(client, healthUrl, stoppingToken).ConfigureAwait(false) ? 0 : failures + 1;
        }

        LogHung(HungAfterFailedChecks);
        return $"llama-server did not respond to {HungAfterFailedChecks} health checks in a row.";
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

    /// <summary>Logs that the local model failed and will not restart.</summary>
    /// <param name="reason">The cause.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "The local model failed, and the engine will not start llama-server again until the engine restarts. Cause: {Reason}")]
    private partial void LogFailed(string reason);

    /// <summary>Logs a server that stopped responding while still running.</summary>
    /// <param name="failedChecks">The number of failed health checks in a row.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "llama-server failed {FailedChecks} health checks in a row.")]
    private partial void LogHung(int failedChecks);

    /// <summary>Logs an unexpected failure while running the server.</summary>
    /// <param name="exception">The cause.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "The llama-server run failed unexpectedly.")]
    private partial void LogRunFailed(Exception exception);

    /// <summary>Logs a scheduled restart.</summary>
    /// <param name="delay">The wait before the restart.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "The engine will restart llama-server after {Delay}.")]
    private partial void LogRestarting(TimeSpan delay);

    /// <summary>Logs one line of server output. This is Debug level, because a verbose server could print prompts.</summary>
    /// <param name="line">The line.</param>
    [LoggerMessage(Level = LogLevel.Debug, Message = "llama-server: {Line}")]
    private partial void LogServerOutput(string line);

    /// <summary>The end of one llama-server run.</summary>
    /// <param name="BecameReady">Whether the model loaded before the run ended.</param>
    /// <param name="Cause">Why the run ended, as text that a person can read.</param>
    private sealed record RunResult(bool BecameReady, string Cause);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>One llama-server process. It sends the output to the log, and keeps the last error line until the model has loaded.</summary>
internal sealed partial class LlamaServerProcess : IDisposable
{
    /// <summary>The longest wait for the rest of the output after the process exits. A child process of llama-server can keep the output open.</summary>
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait for the process to exit after the engine stops it.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The longest error line that the engine keeps.</summary>
    private const int MaxErrorLineLength = 500;

    /// <summary>The process.</summary>
    private readonly Process process;

    /// <summary>The log for the output and for lifecycle events.</summary>
    private readonly ILogger logger;

    /// <summary>The last error line that llama-server wrote before the model loaded.</summary>
    private volatile string? lastStartupError;

    /// <summary>Whether the output handlers keep error lines. This is true only before the model loads, so the engine never keeps a line that can hold a prompt.</summary>
    private volatile bool keepStartupErrors = true;

    /// <summary>Initializes a new instance of the <see cref="LlamaServerProcess"/> class. The process does not start yet.</summary>
    /// <param name="program">The llama-server path.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="apiKey">The key for this run.</param>
    /// <param name="logger">The log.</param>
    public LlamaServerProcess(string program, IReadOnlyList<string> arguments, string apiKey, ILogger logger)
    {
        this.logger = logger;
        var folder = Path.GetDirectoryName(program)!;
        var startInfo = new ProcessStartInfo(program)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        LlamaEnvironment.Prepare(startInfo, folder, apiKey);
        process = new Process { StartInfo = startInfo };

        // The engine reads both outputs, because a full pipe blocks the server.
        process.OutputDataReceived += (_, line) => HandleOutput(line.Data);
        process.ErrorDataReceived += (_, line) => HandleOutput(line.Data);
    }

    /// <summary>Gets the process ID.</summary>
    public int Id => process.Id;

    /// <summary>Gets a value indicating whether the process has exited.</summary>
    public bool HasExited => process.HasExited;

    /// <summary>Gets the exit code. Read it only after the process has exited.</summary>
    public int ExitCode => process.ExitCode;

    /// <summary>Gets the last error line from before the model loaded, if any.</summary>
    public string? LastStartupError => lastStartupError;

    /// <summary>Starts the process, and starts reading its output.</summary>
    /// <exception cref="Win32Exception">The system could not start the program.</exception>
    /// <exception cref="InvalidOperationException">The process could not start.</exception>
    public void Start()
    {
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>Builds the text of the PID file. The start time identifies the process, because the system can give the same ID to a later process.</summary>
    /// <returns>The text, or <see langword="null"/> when the process has already exited, so that its start time is not available.</returns>
    public string? TryFormatPidRecord()
    {
        try
        {
            return string.Create(CultureInfo.InvariantCulture, $"{process.Id} {process.StartTime.ToUniversalTime().Ticks}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Stops keeping error lines, because the model has loaded and later lines can hold prompts.</summary>
    public void StopKeepingErrors() => keepStartupErrors = false;

    /// <summary>Waits until the process exits.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>A task that completes when the process has exited.</returns>
    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

    /// <summary>Waits a short time for the rest of the output of a process that has exited, so that the last error line is complete.</summary>
    /// <param name="cancellationToken">Signals engine shutdown.</param>
    /// <returns>A task that completes when the output has ended, or when the time ends.</returns>
    public async Task DrainOutputAsync(CancellationToken cancellationToken)
    {
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drain.CancelAfter(OutputDrainTimeout);
        try
        {
            await process.WaitForExitAsync(drain.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The output did not end in time. The engine uses the lines that it has.
        }
    }

    /// <summary>Stops the whole process tree and waits a limited time for it to exit.</summary>
    /// <returns>A task that completes when the process has exited, or when the time ends.</returns>
    public async Task StopAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var wait = new CancellationTokenSource(StopTimeout);
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogStopSlow(StopTimeout);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or AggregateException)
        {
            // The process exited between the check and the kill, or the engine could not stop it. This code cannot correct either problem.
        }
    }

    /// <inheritdoc/>
    public void Dispose() => process.Dispose();

    /// <summary>Handles one line of server output. The line goes to the log at Debug level. Before the model loads, the engine also keeps the last error line.</summary>
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
            // A limit on the length keeps the log short.
            var trimmed = line.Trim();
            lastStartupError = trimmed.Length <= MaxErrorLineLength ? trimmed : trimmed[..MaxErrorLineLength];
        }
    }

    /// <summary>Logs one line of server output. This is Debug level, because a verbose server could print prompts.</summary>
    /// <param name="line">The line.</param>
    [LoggerMessage(Level = LogLevel.Debug, Message = "llama-server: {Line}")]
    private partial void LogServerOutput(string line);

    /// <summary>Logs a process that did not exit in time after the engine stopped it.</summary>
    /// <param name="timeout">The wait.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "llama-server did not exit within {Timeout} after the engine stopped it.")]
    private partial void LogStopSlow(TimeSpan timeout);
}

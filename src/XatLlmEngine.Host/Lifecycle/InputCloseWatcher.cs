using Microsoft.Extensions.Options;
using XatLlmEngine.Infrastructure;

namespace XatLlmEngine.Host.Lifecycle;

/// <summary>
/// Stops the engine when its standard input closes. The system closes the input when the parent app stops, also after a crash.
/// So a parent app that keeps the input open has a clean stop on every system. See ADR 0001.
/// </summary>
/// <param name="options">The engine settings.</param>
/// <param name="lifetime">Stops the engine.</param>
/// <param name="logger">The log.</param>
internal sealed partial class InputCloseWatcher(
    IOptions<EngineOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<InputCloseWatcher> logger) : IHostedService
{
    /// <summary>When the setting is on, starts to read the input after the engine has fully started.</summary>
    /// <remarks>
    /// A stop request during the startup cancels the startup, and the engine then ends with an unhandled exception. So the
    /// watch starts only at <see cref="IHostApplicationLifetime.ApplicationStarted"/>. An input that closed earlier stops the engine then.
    /// </remarks>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.StopWhenInputCloses)
        {
            // A read of the standard input cannot be cancelled. A background thread does not keep the process alive, so the engine never waits for it.
            lifetime.ApplicationStarted.Register(() =>
                new Thread(() => Watch(Console.OpenStandardInput())) { IsBackground = true, Name = "input-close-watcher" }.Start());
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Reads the input until it ends, and then stops the engine. The engine ignores the data.</summary>
    /// <param name="input">The standard input.</param>
    internal void Watch(Stream input)
    {
        var buffer = new byte[256];
        try
        {
            while (input.Read(buffer) > 0)
            {
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // A broken input also means that the parent app is gone.
        }

        LogInputClosed();
        lifetime.StopApplication();
    }

    /// <summary>Logs that the input closed.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "The standard input closed, so the engine stops.")]
    private partial void LogInputClosed();
}

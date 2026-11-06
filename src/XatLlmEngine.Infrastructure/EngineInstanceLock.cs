using Microsoft.Extensions.Hosting;

namespace XatLlmEngine.Infrastructure;

/// <summary>Makes sure that only one engine runs for each install folder.</summary>
/// <remarks>
/// A second engine from the same folder would stop the first engine's llama-server through the shared PID file, and both
/// would write the same usage file. The lock is an open file with no sharing. The system frees it when the process ends,
/// also after a crash, so a killed engine leaves no stale lock.
/// </remarks>
/// <param name="dataFolder">The engine's data folder.</param>
internal sealed class EngineInstanceLock(EngineDataFolder dataFolder) : IHostedLifecycleService, IDisposable
{
    /// <summary>The name of the lock file.</summary>
    internal const string FileName = "instance.lock";

    /// <summary>The open lock file, or <see langword="null"/> before the engine starts.</summary>
    private FileStream? lockFile;

    /// <summary>Takes the lock before any other part of the engine starts.</summary>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="InvalidOperationException">Another engine from this install folder holds the lock.</exception>
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        Acquire();
        return Task.CompletedTask;
    }

    /// <summary>Takes the lock.</summary>
    /// <exception cref="InvalidOperationException">Another engine from this install folder holds the lock.</exception>
    public void Acquire()
    {
        var path = dataFolder.File(FileName);
        try
        {
            lockFile = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Another engine from this folder ({AppContext.BaseDirectory}) is running. Stop it first, or start the second engine from a copy in another folder.",
                exception);
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Frees the lock after every other part of the engine has stopped.</summary>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lockFile?.Dispose();
        lockFile = null;
    }
}

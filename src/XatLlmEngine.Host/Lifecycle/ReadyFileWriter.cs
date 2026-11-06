using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using XatLlmEngine.Host.Contracts;
using XatLlmEngine.Infrastructure;

namespace XatLlmEngine.Host.Lifecycle;

/// <summary>
/// Writes the ready file when the API starts, and deletes it when the engine stops. A parent app reads the file to find the
/// address of the engine. This also works when the system chose the port (port 0).
/// </summary>
/// <param name="options">The engine settings.</param>
/// <param name="server">The web server, which knows the real addresses.</param>
/// <param name="lifetime">Tells when the API has started and when the engine stops.</param>
/// <param name="logger">The log.</param>
internal sealed partial class ReadyFileWriter(
    IOptions<EngineOptions> options,
    IServer server,
    IHostApplicationLifetime lifetime,
    ILogger<ReadyFileWriter> logger) : IHostedService
{
    /// <summary>The path of the ready file, or an empty value when the setting is off.</summary>
    private readonly string path = options.Value.ReadyFile;

    /// <summary>Makes the write and the delete happen one after the other. The stop can start on another thread before the write.</summary>
    private readonly Lock sync = new();

    /// <summary>Whether this engine wrote the file. The engine deletes only a file that it wrote. Read and write it only under <see cref="sync"/>.</summary>
    private bool written;

    /// <summary>Whether the engine is stopping. Then the engine does not write the file. Read and write it only under <see cref="sync"/>.</summary>
    private bool stopping;

    /// <summary>Removes an old ready file, and prepares the write when the API starts.</summary>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (path.Length > 0)
        {
            // A killed engine leaves its file. A parent app must never read the address of an engine that is gone.
            Remove(path);
            Remove(path + ".tmp");
            lifetime.ApplicationStarted.Register(Write);
            lifetime.ApplicationStopping.Register(Delete);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Writes the file. When the engine cannot write it, the engine stops, because the parent app would wait for the file for ever.</summary>
    private void Write()
    {
        var url = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (url is null)
        {
            LogNoAddress(path);
            Fail();
            return;
        }

        try
        {
            lock (sync)
            {
                if (stopping)
                {
                    return;
                }

                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new ReadyFile(url, Environment.ProcessId, EngineVersion.Value), HostJson.Default.ReadyFile));
                written = true;
            }

            LogWritten(path, url);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            LogWriteFailed(exception, path);
            Remove(path + ".tmp");
            Fail();
        }
    }

    /// <summary>Stops the engine with a failure exit code, so that the parent app can tell this stop from a normal stop.</summary>
    private void Fail()
    {
        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }

    /// <summary>Deletes the file that this engine wrote, so that a parent app does not read an old address.</summary>
    private void Delete()
    {
        lock (sync)
        {
            stopping = true;
            if (written)
            {
                Remove(path);
            }
        }
    }

    /// <summary>Deletes a file, if it exists.</summary>
    /// <param name="file">The file path.</param>
    private void Remove(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            LogDeleteFailed(exception, file);
        }
    }

    /// <summary>Logs that the ready file is written.</summary>
    /// <param name="file">The file path.</param>
    /// <param name="address">The address in the file.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "The engine wrote the ready file {File}. The API listens on {Address}.")]
    private partial void LogWritten(string file, string address);

    /// <summary>Logs that the server has no address to write.</summary>
    /// <param name="file">The file path.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "The engine cannot write the ready file {File}, because the server has no address. The engine stops.")]
    private partial void LogNoAddress(string file);

    /// <summary>Logs that the ready file cannot be written.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="file">The file path.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "The engine cannot write the ready file {File}. The engine stops.")]
    private partial void LogWriteFailed(Exception exception, string file);

    /// <summary>Logs that the ready file cannot be deleted.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="file">The file path.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "The engine cannot delete the ready file {File}.")]
    private partial void LogDeleteFailed(Exception exception, string file);
}

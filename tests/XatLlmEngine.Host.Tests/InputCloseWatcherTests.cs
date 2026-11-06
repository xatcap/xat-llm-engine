using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XatLlmEngine.Host.Lifecycle;
using XatLlmEngine.Infrastructure;

namespace XatLlmEngine.Host.Tests;

/// <summary>Tests the stop when the standard input closes.</summary>
public class InputCloseWatcherTests
{
    /// <summary>The engine stops when the input ends, after it has read all data.</summary>
    [Fact]
    public void Watch_InputEnds_StopsEngine()
    {
        var lifetime = new RecordingLifetime();
        var watcher = new InputCloseWatcher(Options.Create(new EngineOptions { StopWhenInputCloses = true }), lifetime, NullLogger<InputCloseWatcher>.Instance);

        watcher.Watch(new MemoryStream(new byte[1000]));

        Assert.True(lifetime.StopRequested);
    }

    /// <summary>A broken input also stops the engine, because the parent app is gone.</summary>
    [Fact]
    public void Watch_InputBreaks_StopsEngine()
    {
        var lifetime = new RecordingLifetime();
        var watcher = new InputCloseWatcher(Options.Create(new EngineOptions { StopWhenInputCloses = true }), lifetime, NullLogger<InputCloseWatcher>.Instance);
        var input = new MemoryStream();
        input.Dispose();

        watcher.Watch(input);

        Assert.True(lifetime.StopRequested);
    }

    /// <summary>When the setting is off, the engine does not read the input, also after the engine has started.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Start_SettingOff_DoesNotStopEngine()
    {
        var lifetime = new RecordingLifetime();
        var watcher = new InputCloseWatcher(Options.Create(new EngineOptions()), lifetime, NullLogger<InputCloseWatcher>.Instance);

        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await lifetime.StartedSource.CancelAsync();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.False(lifetime.StopRequested);
    }

    /// <summary>Records a request to stop.</summary>
    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        /// <summary>Whether the engine was told to stop. Another thread can set it.</summary>
        private volatile bool stopRequested;

        /// <summary>Gets a value indicating whether the engine was told to stop.</summary>
        public bool StopRequested => stopRequested;

        /// <summary>Gets the source that signals the end of the startup.</summary>
        public CancellationTokenSource StartedSource { get; } = new();

        /// <inheritdoc/>
        public CancellationToken ApplicationStarted => StartedSource.Token;

        /// <inheritdoc/>
        public CancellationToken ApplicationStopping => CancellationToken.None;

        /// <inheritdoc/>
        public CancellationToken ApplicationStopped => CancellationToken.None;

        /// <inheritdoc/>
        public void StopApplication() => stopRequested = true;
    }
}

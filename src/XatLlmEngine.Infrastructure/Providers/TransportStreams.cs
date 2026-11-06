namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>The provider did not send the response headers in time.</summary>
/// <param name="timeout">The time limit.</param>
/// <param name="innerException">The cancellation that the time limit caused.</param>
internal sealed class ProviderTimeoutException(TimeSpan timeout, Exception innerException)
    : TimeoutException($"The provider did not start its answer within {timeout.TotalSeconds:0} seconds.", innerException)
{
    /// <summary>Gets the time limit.</summary>
    public TimeSpan Timeout { get; } = timeout;
}

/// <summary>The provider sent no data for longer than the idle limit, while the engine read the response body.</summary>
/// <param name="timeout">The idle limit.</param>
internal sealed class ProviderStalledException(TimeSpan timeout)
    : TimeoutException($"The provider sent no data for {timeout.TotalSeconds:0} seconds.")
{
    /// <summary>Gets the idle limit.</summary>
    public TimeSpan Timeout { get; } = timeout;
}

/// <summary>A read-only stream that fails when one read waits longer than the idle limit.</summary>
/// <remarks>A provider can send the headers and then stop. Without this limit, the engine would wait until the caller disconnects.</remarks>
internal sealed class IdleTimeoutStream : Stream
{
    /// <summary>The response stream.</summary>
    private readonly Stream inner;

    /// <summary>The idle limit.</summary>
    private readonly TimeSpan timeout;

    /// <summary>The clock of the idle limit.</summary>
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a new instance of the <see cref="IdleTimeoutStream"/> class.</summary>
    /// <param name="inner">The response stream. This stream takes ownership of it.</param>
    /// <param name="timeout">The longest wait for one read.</param>
    /// <param name="timeProvider">The clock.</param>
    public IdleTimeoutStream(Stream inner, TimeSpan timeout, TimeProvider timeProvider)
    {
        this.inner = inner;
        this.timeout = timeout;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Each read has its own timer, so a timer that ends just after a read cannot affect the next read.
        using var idle = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);
        try
        {
            return await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ProviderStalledException(timeout);
        }
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A read-only stream that fails when the data is larger than a limit, so that a provider cannot fill the engine's memory.</summary>
/// <param name="inner">The stream to read. This stream takes ownership of it.</param>
/// <param name="limit">The largest number of bytes to accept.</param>
internal sealed class SizeLimitedStream(Stream inner, long limit) : Stream
{
    /// <summary>The number of bytes read so far.</summary>
    private long total;

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The data is larger than the limit.</exception>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        total += read;
        return total > limit ? throw new InvalidDataException($"The response is larger than {limit} bytes.") : read;
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

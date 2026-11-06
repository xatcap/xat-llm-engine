using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Lets at most one request per slot reach llama-server. Other requests wait in a bounded queue, in order of arrival.</summary>
/// <remarks>
/// Without this gate, every waiting request would hold a connection and an HTTP thread of llama-server. Then the health
/// checks can get no answer under load, and a working server looks hung. See ADR 0009.
/// </remarks>
internal sealed class LocalRequestGate
{
    /// <summary>Protects the counts and the queue.</summary>
    private readonly Lock sync = new();

    /// <summary>The requests that wait for a slot, in order of arrival.</summary>
    private readonly LinkedList<TaskCompletionSource<EngineError?>> waiters = [];

    /// <summary>The clock, for the time of the last progress.</summary>
    private readonly TimeProvider timeProvider;

    /// <summary>The number of requests that have a slot.</summary>
    private int active;

    /// <summary>The error for new requests, or <see langword="null"/> when the server can take requests.</summary>
    private EngineError? closed;

    /// <summary>The timestamp of the last data from llama-server.</summary>
    private long lastProgress;

    /// <summary>Initializes a new instance of the <see cref="LocalRequestGate"/> class. The gate is closed until the model is ready.</summary>
    /// <param name="capacity">The number of slots.</param>
    /// <param name="queueLimit">The number of requests that can wait.</param>
    /// <param name="timeProvider">The clock.</param>
    public LocalRequestGate(int capacity, int queueLimit, TimeProvider timeProvider)
    {
        Capacity = capacity;
        QueueLimit = queueLimit;
        this.timeProvider = timeProvider;
        lastProgress = timeProvider.GetTimestamp();
        closed = LocalModelErrors.ForNewRequest(new LlamaServerSnapshot(LlamaServerState.Starting));
    }

    /// <summary>Gets the number of slots.</summary>
    public int Capacity { get; }

    /// <summary>Gets the number of requests that can wait.</summary>
    public int QueueLimit { get; }

    /// <summary>Gets the number of requests that have a slot.</summary>
    public int Active
    {
        get
        {
            lock (sync)
            {
                return active;
            }
        }
    }

    /// <summary>Gets the number of requests that wait for a slot.</summary>
    public int Queued
    {
        get
        {
            lock (sync)
            {
                return waiters.Count;
            }
        }
    }

    /// <summary>Gets the timestamp of the last data from llama-server, from <see cref="TimeProvider.GetTimestamp"/>.</summary>
    public long LastProgress => Interlocked.Read(ref lastProgress);

    /// <summary>Records that llama-server sent data, so that the hang check knows that the server works.</summary>
    public void ReportProgress() => Interlocked.Exchange(ref lastProgress, timeProvider.GetTimestamp());

    /// <summary>Waits for a slot.</summary>
    /// <param name="cancellationToken">Stops the wait, for example when the caller disconnects. The request then leaves the queue.</param>
    /// <returns>A lease that the caller must dispose, or the error when the server cannot take the request.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled the wait.</exception>
    public async Task<Result<Lease>> EnterAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<EngineError?> waiter;
        LinkedListNode<TaskCompletionSource<EngineError?>> node;
        lock (sync)
        {
            if (closed is { } error)
            {
                return Result<Lease>.Fail(error);
            }

            if (active < Capacity)
            {
                active++;
                return Result<Lease>.Ok(new Lease(this));
            }

            if (waiters.Count >= QueueLimit)
            {
                return Result<Lease>.Fail(LocalModelErrors.QueueFull);
            }

            waiter = new TaskCompletionSource<EngineError?>(TaskCreationOptions.RunContinuationsAsynchronously);
            node = waiters.AddLast(waiter);
        }

        await using var registration = cancellationToken.Register(() => Leave(node, cancellationToken)).ConfigureAwait(false);
        var failure = await waiter.Task.ConfigureAwait(false);
        return failure is null ? Result<Lease>.Ok(new Lease(this)) : Result<Lease>.Fail(failure);
    }

    /// <summary>Opens or closes the gate when the state of the local model changes. Closing fails every waiting request. Requests that have a slot continue.</summary>
    /// <param name="snapshot">The new state.</param>
    public void Update(LlamaServerSnapshot snapshot)
    {
        var error = LocalModelErrors.ForNewRequest(snapshot);
        List<TaskCompletionSource<EngineError?>> failed = [];
        lock (sync)
        {
            closed = error;
            if (error is not null)
            {
                failed.AddRange(waiters);
                waiters.Clear();
            }
            else
            {
                // A recovered server can take the waiting requests again.
                while (active < Capacity && waiters.First is { } next)
                {
                    waiters.RemoveFirst();
                    active++;
                    next.Value.TrySetResult(null);
                }
            }
        }

        foreach (var waiter in failed)
        {
            waiter.TrySetResult(error);
        }
    }

    /// <summary>Removes a waiting request after its caller canceled the wait.</summary>
    /// <param name="node">The request's place in the queue.</param>
    /// <param name="cancellationToken">The canceled token.</param>
    private void Leave(LinkedListNode<TaskCompletionSource<EngineError?>> node, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (node.List is null)
            {
                // The request already has a slot or an error.
                return;
            }

            waiters.Remove(node);
        }

        node.Value.TrySetCanceled(cancellationToken);
    }

    /// <summary>Frees a slot, or gives it to the next waiting request.</summary>
    private void Release()
    {
        TaskCompletionSource<EngineError?>? next = null;
        lock (sync)
        {
            if (closed is null && waiters.First is { } first)
            {
                waiters.RemoveFirst();
                next = first.Value;
            }
            else
            {
                active--;
            }
        }

        // The slot moves to the next request, so the count does not change.
        if (next is not null && !next.TrySetResult(null))
        {
            Release();
        }
    }

    /// <summary>A slot for one request. Dispose it when llama-server has finished the request.</summary>
    /// <param name="gate">The gate that gave the slot.</param>
    internal sealed class Lease(LocalRequestGate gate) : IDisposable
    {
        /// <summary>1 after the lease has been released.</summary>
        private int released;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

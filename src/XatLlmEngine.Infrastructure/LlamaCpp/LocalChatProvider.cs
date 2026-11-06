using Microsoft.Extensions.Options;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Serves chat requests from the local model through the supervised llama-server.</summary>
/// <param name="supervisor">Reports whether and where the server is running.</param>
/// <param name="gate">Lets at most one request per slot reach the server.</param>
/// <param name="client">The protocol client, because llama-server speaks the OpenAI protocol.</param>
/// <param name="options">The local model settings.</param>
internal sealed class LocalChatProvider(
    LlamaServerSupervisor supervisor,
    LocalRequestGate gate,
    OpenAiClient client,
    IOptions<LlamaCppOptions> options) : IChatProvider
{
    /// <summary>The reserved provider name of the local model.</summary>
    public const string ProviderName = "local";

    /// <inheritdoc/>
    public string Name => ProviderName;

    /// <inheritdoc/>
    public IReadOnlyList<string> Models { get; } = [options.Value.ModelName];

    /// <inheritdoc/>
    public IReadOnlyList<string> RequestFields => options.Value.RequestFields;

    /// <inheritdoc/>
    public int MaxTokensLimit => options.Value.MaxTokensLimit;

    /// <inheritdoc/>
    public ProviderStatus Status
    {
        get
        {
            var load = new ProviderLoad(gate.Active, gate.Queued, gate.Capacity, gate.QueueLimit);
            var state = supervisor.State switch
            {
                LlamaServerState.Ready => load.Active >= load.Capacity ? ProviderState.Busy : ProviderState.Ready,
                LlamaServerState.Starting => ProviderState.Starting,
                LlamaServerState.Unresponsive => ProviderState.Unresponsive,
                LlamaServerState.Restarting => ProviderState.Restarting,
                LlamaServerState.Missing => ProviderState.Missing,
                LlamaServerState.Stopped => ProviderState.Stopping,
                LlamaServerState.Failed => ProviderState.Failed,
            };
            return new ProviderStatus(state, load);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<ChatCompletion>> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        if (FindModelProblem(request) is { } modelProblem)
        {
            return Result<ChatCompletion>.Fail(modelProblem);
        }

        var entered = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        if (!entered.IsSuccess)
        {
            return Result<ChatCompletion>.Fail(entered.Error);
        }

        using var lease = entered.Value;
        if (Serving() is not { } endpoint)
        {
            return Result<ChatCompletion>.Fail(LocalModelErrors.ForNewRequest(supervisor.Snapshot) ?? NotServing);
        }

        var result = await client.CompleteAsync(endpoint.BaseUrl, endpoint.ApiKey, WithDefaults(request), cancellationToken).ConfigureAwait(false);
        gate.ReportProgress();
        return result;
    }

    /// <inheritdoc/>
    public async Task<Result<IAsyncEnumerable<ChatChunk>>> StreamAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        if (FindModelProblem(request) is { } modelProblem)
        {
            return Result<IAsyncEnumerable<ChatChunk>>.Fail(modelProblem);
        }

        var entered = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        if (!entered.IsSuccess)
        {
            return Result<IAsyncEnumerable<ChatChunk>>.Fail(entered.Error);
        }

        var lease = entered.Value;
        try
        {
            if (Serving() is not { } endpoint)
            {
                lease.Dispose();
                return Result<IAsyncEnumerable<ChatChunk>>.Fail(LocalModelErrors.ForNewRequest(supervisor.Snapshot) ?? NotServing);
            }

            var stream = await client.StreamAsync(endpoint.BaseUrl, endpoint.ApiKey, WithDefaults(request), cancellationToken).ConfigureAwait(false);
            if (!stream.IsSuccess)
            {
                lease.Dispose();
                return stream;
            }

            // The slot stays in use until the caller disposes the stream.
            return Result<IAsyncEnumerable<ChatChunk>>.Ok(new LeasedStream(stream.Value, lease, gate));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>The error when the server stopped after the request got its slot.</summary>
    private static EngineError NotServing => new(ErrorKind.Unavailable, "llama-server stopped. Try again shortly.");

    /// <summary>Reads the address of a server that can take requests. A request that already has its slot can continue on a server that does not answer health checks.</summary>
    /// <returns>The address, or <see langword="null"/> when the server is not running.</returns>
    private LlamaServerEndpoint? Serving() =>
        supervisor.Snapshot is { State: LlamaServerState.Ready or LlamaServerState.Unresponsive, Endpoint: { } endpoint } ? endpoint : null;

    /// <summary>Checks the model name, because llama-server ignores it.</summary>
    /// <param name="request">The validated request.</param>
    /// <returns>The problem, or <see langword="null"/> when the name is correct.</returns>
    private EngineError? FindModelProblem(ChatRequest request) =>
        Models.Contains(request.Model, StringComparer.Ordinal)
            ? null
            : new EngineError(ErrorKind.NotFound, $"The local model '{request.Model}' does not exist. Use '{options.Value.ModelName}'.");

    /// <summary>Applies the local default temperature when the caller sent none.</summary>
    /// <param name="request">The validated request.</param>
    /// <returns>The request to send.</returns>
    private ChatRequest WithDefaults(ChatRequest request)
    {
        // The request owns its body, so the provider can change it.
        request.Body["temperature"] ??= options.Value.DefaultTemperature;
        return request;
    }

    /// <summary>A stream that holds a slot of the gate until the caller disposes its enumerator. The Application layer always reads the first piece, so it always disposes the enumerator.</summary>
    /// <param name="inner">The stream from llama-server.</param>
    /// <param name="lease">The slot.</param>
    /// <param name="gate">The gate, which records each piece as progress.</param>
    private sealed class LeasedStream(IAsyncEnumerable<ChatChunk> inner, LocalRequestGate.Lease lease, LocalRequestGate gate) : IAsyncEnumerable<ChatChunk>
    {
        /// <inheritdoc/>
        public IAsyncEnumerator<ChatChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(inner.GetAsyncEnumerator(cancellationToken), lease, gate);

        /// <summary>Reads the pieces, and frees the slot when it is disposed.</summary>
        /// <param name="source">The pieces from llama-server.</param>
        /// <param name="lease">The slot.</param>
        /// <param name="gate">The gate.</param>
        private sealed class Enumerator(IAsyncEnumerator<ChatChunk> source, LocalRequestGate.Lease lease, LocalRequestGate gate) : IAsyncEnumerator<ChatChunk>
        {
            /// <inheritdoc/>
            public ChatChunk Current => source.Current;

            /// <inheritdoc/>
            public async ValueTask<bool> MoveNextAsync()
            {
                var hasNext = await source.MoveNextAsync().ConfigureAwait(false);
                gate.ReportProgress();
                return hasNext;
            }

            /// <inheritdoc/>
            public async ValueTask DisposeAsync()
            {
                try
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    lease.Dispose();
                }
            }
        }
    }
}

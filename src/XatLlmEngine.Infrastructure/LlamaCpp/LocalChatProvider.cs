using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Serves chat requests from the local model through the supervised llama-server.</summary>
/// <param name="supervisor">Reports whether and where the server is running.</param>
/// <param name="client">The protocol client, because llama-server speaks the OpenAI protocol.</param>
/// <param name="options">The local model settings.</param>
internal sealed class LocalChatProvider(
    LlamaServerSupervisor supervisor,
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
    public string? InitialSystemPrompt => options.Value.SystemPrompt;

    /// <inheritdoc/>
    public int MaxSystemPromptLength => options.Value.MaxSystemPromptLength;

    /// <inheritdoc/>
    public Result<JsonObject> ApplySystemPrompt(JsonObject body, string? prompt) => OpenAiClient.ApplySystemPrompt(body, prompt);

    /// <inheritdoc/>
    public Task<Result<ChatCompletion>> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        // One reading of the endpoint, because a crash can clear it at any moment.
        var endpoint = supervisor.Endpoint;
        var problem = FindProblem(request, endpoint);
        return problem is not null
            ? Task.FromResult(Result<ChatCompletion>.Fail(problem))
            : client.CompleteAsync(endpoint!.BaseUrl, endpoint.ApiKey, WithDefaults(request), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<Result<IAsyncEnumerable<ChatChunk>>> StreamAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        // One reading of the endpoint, because a crash can clear it at any moment.
        var endpoint = supervisor.Endpoint;
        var problem = FindProblem(request, endpoint);
        return problem is not null
            ? Task.FromResult(Result<IAsyncEnumerable<ChatChunk>>.Fail(problem))
            : client.StreamAsync(endpoint!.BaseUrl, endpoint.ApiKey, WithDefaults(request), cancellationToken);
    }

    /// <summary>Checks that the engine can serve the request. This method checks the model name because llama-server ignores it.</summary>
    /// <param name="request">The validated request.</param>
    /// <param name="endpoint">The server's current address, if it is ready.</param>
    /// <returns>The problem, or <see langword="null"/> when the request can go ahead.</returns>
    private EngineError? FindProblem(ChatRequest request, LlamaServerEndpoint? endpoint)
    {
        if (!Models.Contains(request.Model, StringComparer.Ordinal))
        {
            return new EngineError(ErrorKind.NotFound, $"The local model '{request.Model}' does not exist. Use '{options.Value.ModelName}'.");
        }

        if (endpoint is not null)
        {
            return null;
        }

        return new EngineError(
            ErrorKind.Unavailable,
            supervisor.State == LlamaServerState.Missing
                ? "The local model is not installed. Download it from the GitHub release into the models folder, then restart the engine."
                : "The local model is still loading. Try again shortly.");
    }

    /// <summary>Applies the local default temperature when the caller sent none.</summary>
    /// <param name="request">The validated request.</param>
    /// <returns>The request to send.</returns>
    private ChatRequest WithDefaults(ChatRequest request)
    {
        var body = request.Body.DeepClone().AsObject();
        body["temperature"] ??= options.Value.DefaultTemperature;
        return request with { Body = body };
    }
}

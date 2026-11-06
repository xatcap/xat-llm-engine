using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.Providers.Anthropic;

/// <summary>Serves chat requests through the Anthropic Messages API.</summary>
/// <remarks>Callers send Anthropic's own request format, and this adapter converts the answer to the engine's unified response.</remarks>
/// <param name="name">The provider's name.</param>
/// <param name="options">The provider's settings. These settings must include its base URL and key.</param>
/// <param name="transport">Sends the requests and translates transport failures.</param>
internal sealed class AnthropicChatProvider(string name, ProviderOptions options, ProviderTransport transport) : IChatProvider
{
    /// <summary>The API version that this adapter was built for.</summary>
    private const string ApiVersion = "2023-06-01";

    /// <inheritdoc/>
    public string Name => name;

    /// <inheritdoc/>
    public IReadOnlyList<string> Models => options.Models;

    /// <inheritdoc/>
    public IReadOnlyList<string> RequestFields => options.RequestFields;

    /// <inheritdoc/>
    public int MaxTokensLimit => options.MaxTokensLimit;

    /// <inheritdoc/>
    public TokenBudget? Budget => options.TokenBudget > 0 ? new TokenBudget(options.TokenBudget, TimeSpan.FromHours(options.BudgetPeriodHours)) : null;

    /// <inheritdoc/>
    public Task<Result<ChatCompletion>> CompleteAsync(ChatRequest request, CancellationToken cancellationToken) =>
        transport.CompleteAsync(
            Operation(),
            Authenticate,
            ToWire(request, stream: false),
            ProviderJson.Default.AnthropicResponse,
            ToCompletion,
            cancellationToken);

    /// <inheritdoc/>
    public Task<Result<IAsyncEnumerable<ChatChunk>>> StreamAsync(ChatRequest request, CancellationToken cancellationToken) =>
        transport.StreamAsync(Operation(), Authenticate, ToWire(request, stream: true), ReadChunksAsync, cancellationToken);

    /// <summary>Converts Anthropic's stop reason to the engine's reason.</summary>
    /// <param name="stopReason">The wire value.</param>
    /// <returns>The engine's reason. Natural endings and stop sequences both count as a normal stop.</returns>
    private static FinishReason ToFinishReason(string? stopReason) => stopReason switch
    {
        "max_tokens" => FinishReason.Length,
        "refusal" => FinishReason.ContentFilter,
        _ => FinishReason.Stop,
    };

    /// <summary>Builds the address of the Messages operation.</summary>
    /// <returns>The address.</returns>
    private Uri Operation() => new($"{options.BaseUrl!.ToString().TrimEnd('/')}/v1/messages");

    /// <summary>Adds the key and the API version.</summary>
    /// <param name="headers">The request headers.</param>
    private void Authenticate(HttpRequestHeaders headers)
    {
        headers.Add("x-api-key", options.ApiKey);
        headers.Add("anthropic-version", ApiVersion);
    }

    /// <summary>Converts a complete answer.</summary>
    /// <param name="body">The wire answer.</param>
    /// <returns>The answer, or a failure when the answer has no content.</returns>
    private Result<ChatCompletion> ToCompletion(AnthropicResponse? body)
    {
        if (body?.Content is null)
        {
            return Result<ChatCompletion>.Fail(transport.Malformed());
        }

        var text = string.Concat(body.Content.Where(block => block.Type == "text").Select(block => block.Text));
        var usage = body.Usage is null ? null : new TokenUsage(body.Usage.InputTokens ?? 0, body.Usage.OutputTokens ?? 0);
        return Result<ChatCompletion>.Ok(new ChatCompletion(text, ToFinishReason(body.StopReason), usage));
    }

    /// <summary>Builds the body to send. The caller's fields pass through unchanged. The Application layer has already added the token limit, which the API requires.</summary>
    /// <param name="request">The routed request.</param>
    /// <param name="stream">Whether to ask for a stream.</param>
    /// <returns>The body.</returns>
    private static JsonObject ToWire(ChatRequest request, bool stream)
    {
        // The request owns its body, so the adapter can change it.
        var body = request.Body;
        body["model"] = request.Model;
        if (stream)
        {
            body["stream"] = true;
        }
        else
        {
            body.Remove("stream");
        }

        return body;
    }

    /// <summary>Converts stream events into chunks.</summary>
    /// <param name="events">The server-sent events. The transport releases the connection at the end.</param>
    /// <returns>The chunks that carry something useful.</returns>
    private async IAsyncEnumerable<ChatChunk> ReadChunksAsync(IAsyncEnumerable<SseItem<string>> events)
    {
        var inputTokens = 0;
        await foreach (var item in events.ConfigureAwait(false))
        {
            var streamEvent = Parse(item);
            switch (streamEvent?.Type)
            {
                case "message_start":
                    inputTokens = streamEvent.Message?.Usage?.InputTokens ?? 0;
                    break;
                case "content_block_delta" when streamEvent.Delta is { Type: "text_delta", Text.Length: > 0 }:
                    yield return new ChatChunk(streamEvent.Delta.Text, null, null);
                    break;
                case "message_delta":
                    yield return new ChatChunk(
                        null,
                        ToFinishReason(streamEvent.Delta?.StopReason),
                        new TokenUsage(streamEvent.Usage?.InputTokens ?? inputTokens, streamEvent.Usage?.OutputTokens ?? 0));
                    break;
                case "message_stop":
                    yield break;
            }
        }
    }

    /// <summary>Parses one stream event.</summary>
    /// <param name="item">The raw event.</param>
    /// <returns>The parsed event.</returns>
    /// <exception cref="ChatStreamException">The event reports an error, or the engine cannot read it.</exception>
    private AnthropicEvent? Parse(SseItem<string> item)
    {
        if (item.EventType == "error")
        {
            throw new ChatStreamException(transport.StreamFailed());
        }

        try
        {
            return JsonSerializer.Deserialize(item.Data, ProviderJson.Default.AnthropicEvent);
        }
        catch (JsonException exception)
        {
            throw new ChatStreamException(transport.Malformed(), exception);
        }
    }
}

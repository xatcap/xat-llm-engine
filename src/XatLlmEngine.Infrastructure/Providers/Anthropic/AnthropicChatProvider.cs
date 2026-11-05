using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Polly.Timeout;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.Providers.Anthropic;

/// <summary>Serves chat requests through the Anthropic Messages API.</summary>
/// <remarks>Callers send Anthropic's own request format, and this adapter converts the answer to the engine's unified response.</remarks>
/// <param name="name">The provider's name.</param>
/// <param name="options">The provider's settings. These settings must include its base URL and key.</param>
/// <param name="httpClientFactory">Creates HTTP clients, so that the connection pool works correctly.</param>
/// <param name="httpClientName">The named client, which carries the provider's resilience settings.</param>
/// <param name="timeProvider">The clock, for a Retry-After header that holds a date.</param>
/// <param name="logger">The log for failed responses.</param>
internal sealed class AnthropicChatProvider(
    string name,
    ProviderOptions options,
    IHttpClientFactory httpClientFactory,
    string httpClientName,
    TimeProvider timeProvider,
    ILogger<AnthropicChatProvider> logger) : IChatProvider
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
    public async Task<Result<ChatCompletion>> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(ToWire(request, stream: false), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<ChatCompletion>.Fail(await HttpFailure.FromResponseAsync(name, response, timeProvider.GetUtcNow(), logger, cancellationToken).ConfigureAwait(false));
            }

            var body = await response.Content
                .ReadFromJsonAsync(ProviderJson.Default.AnthropicResponse, cancellationToken)
                .ConfigureAwait(false);
            if (body?.Content is null)
            {
                return Result<ChatCompletion>.Fail(HttpFailure.Malformed(name));
            }

            var text = string.Concat(body.Content.Where(block => block.Type == "text").Select(block => block.Text));
            var usage = body.Usage is null ? null : new TokenUsage(body.Usage.InputTokens ?? 0, body.Usage.OutputTokens ?? 0);
            return Result<ChatCompletion>.Ok(new ChatCompletion(text, ToFinishReason(body.StopReason), usage));
        }
        catch (JsonException)
        {
            return Result<ChatCompletion>.Fail(HttpFailure.Malformed(name));
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            return Result<ChatCompletion>.Fail(HttpFailure.Unreachable(name));
        }
    }

    /// <inheritdoc/>
    public async Task<Result<IAsyncEnumerable<ChatChunk>>> StreamAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(ToWire(request, stream: true), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            return Result<IAsyncEnumerable<ChatChunk>>.Fail(HttpFailure.Unreachable(name));
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                return Result<IAsyncEnumerable<ChatChunk>>.Fail(await HttpFailure.FromResponseAsync(name, response, timeProvider.GetUtcNow(), logger, cancellationToken).ConfigureAwait(false));
            }
        }

        return Result<IAsyncEnumerable<ChatChunk>>.Ok(ReadChunksAsync(response, cancellationToken));
    }

    /// <summary>Converts Anthropic's stop reason to the engine's reason.</summary>
    /// <param name="stopReason">The wire value.</param>
    /// <returns>The engine's reason. Natural endings and stop sequences both count as a normal stop.</returns>
    private static FinishReason ToFinishReason(string? stopReason) => stopReason switch
    {
        "max_tokens" => FinishReason.Length,
        "refusal" => FinishReason.ContentFilter,
        _ => FinishReason.Stop,
    };

    /// <summary>Sends the request and returns as soon as response headers arrive, so that streams are not buffered.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, which the caller must dispose.</returns>
    private async Task<HttpResponseMessage> SendAsync(JsonObject body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"{options.BaseUrl!.ToString().TrimEnd('/')}/v1/messages"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("x-api-key", options.ApiKey);
        message.Headers.Add("anthropic-version", ApiVersion);

        using var client = httpClientFactory.CreateClient(httpClientName);
        return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the body to send. The caller's fields pass through unchanged. If the token limit is missing, this method adds it, because the API requires it.</summary>
    /// <param name="request">The routed request.</param>
    /// <param name="stream">Whether to ask for a stream.</param>
    /// <returns>The body.</returns>
    private JsonObject ToWire(ChatRequest request, bool stream)
    {
        var body = request.Body.DeepClone().AsObject();
        body["model"] = request.Model;
        body["max_tokens"] ??= options.DefaultMaxTokens;
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

    /// <summary>Converts stream events into chunks and releases the connection at the end.</summary>
    /// <param name="response">The successful response, which this method takes ownership of.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The chunks that carry something useful.</returns>
    private async IAsyncEnumerable<ChatChunk> ReadChunksAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var inputTokens = 0;
        await foreach (var item in SseReader.ReadAsync(name, response, cancellationToken).ConfigureAwait(false))
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
            throw new ChatStreamException(new EngineError(ErrorKind.Upstream, $"The provider '{name}' failed during the stream."));
        }

        try
        {
            return JsonSerializer.Deserialize(item.Data, ProviderJson.Default.AnthropicEvent);
        }
        catch (JsonException exception)
        {
            throw new ChatStreamException(HttpFailure.Malformed(name), exception);
        }
    }
}

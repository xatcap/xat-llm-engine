using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

/// <summary>Speaks the OpenAI chat completions protocol. Cloud services and the local llama-server share this code.</summary>
/// <param name="transport">Sends the requests and translates transport failures.</param>
/// <param name="requestStreamUsage">
/// Whether the server accepts <c>stream_options</c>. Then the client always asks for token counts in a stream, for the budget
/// and the metrics. The Application layer gives them to the caller only when the caller asked.
/// </param>
internal sealed class OpenAiClient(ProviderTransport transport, bool requestStreamUsage = false)
{
    /// <summary>The value a server sends as the last stream event.</summary>
    private const string StreamEnd = "[DONE]";

    /// <summary>Generates a complete answer.</summary>
    /// <param name="baseUrl">The server's base address, including any version path.</param>
    /// <param name="apiKey">The key for this server.</param>
    /// <param name="request">The validated request.</param>
    /// <param name="cancellationToken">Cancels generation.</param>
    /// <returns>The answer, or the expected failure.</returns>
    public Task<Result<ChatCompletion>> CompleteAsync(Uri baseUrl, string apiKey, ChatRequest request, CancellationToken cancellationToken) =>
        transport.CompleteAsync(
            Operation(baseUrl),
            headers => Authenticate(headers, apiKey),
            ToWire(request, stream: false),
            ProviderJson.Default.OpenAiChatResponse,
            ToCompletion,
            cancellationToken);

    /// <summary>Starts a streamed answer.</summary>
    /// <param name="baseUrl">The server's base address, including any version path.</param>
    /// <param name="apiKey">The key for this server.</param>
    /// <param name="request">The validated request.</param>
    /// <param name="cancellationToken">Cancels generation.</param>
    /// <returns>The stream, or a failure if generation could not start.</returns>
    public Task<Result<IAsyncEnumerable<ChatChunk>>> StreamAsync(Uri baseUrl, string apiKey, ChatRequest request, CancellationToken cancellationToken)
    {
        var body = ToWire(request, stream: true);
        if (requestStreamUsage)
        {
            var streamOptions = body["stream_options"] as JsonObject ?? [];
            streamOptions["include_usage"] = true;
            body["stream_options"] = streamOptions;
        }

        return transport.StreamAsync(Operation(baseUrl), headers => Authenticate(headers, apiKey), body, ReadChunksAsync, cancellationToken);
    }

    /// <summary>Builds the address of the chat completions operation.</summary>
    /// <param name="baseUrl">The server's base address.</param>
    /// <returns>The address.</returns>
    private static Uri Operation(Uri baseUrl) => new($"{baseUrl.ToString().TrimEnd('/')}/chat/completions");

    /// <summary>Adds the bearer key.</summary>
    /// <param name="headers">The request headers.</param>
    /// <param name="apiKey">The key.</param>
    private static void Authenticate(HttpRequestHeaders headers, string apiKey) =>
        headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    /// <summary>Converts a complete answer.</summary>
    /// <param name="body">The wire answer.</param>
    /// <returns>The answer, or a failure when the answer has no text.</returns>
    private Result<ChatCompletion> ToCompletion(OpenAiChatResponse? body)
    {
        var choice = body?.Choices is [var first, ..] ? first : null;
        return choice?.Message?.Content is { } content
            ? Result<ChatCompletion>.Ok(new ChatCompletion(content, ToFinishReason(choice.FinishReason) ?? FinishReason.Stop, ToUsage(body!.Usage)))
            : Result<ChatCompletion>.Fail(transport.Malformed());
    }

    /// <summary>Converts a provider's finish reason.</summary>
    /// <param name="value">The wire value.</param>
    /// <returns>The engine's reason, or <see langword="null"/> when generation has not finished.</returns>
    private static FinishReason? ToFinishReason(string? value) => value switch
    {
        null => null,
        "length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        _ => FinishReason.Stop,
    };

    /// <summary>Converts token counts.</summary>
    /// <param name="usage">The wire value.</param>
    /// <returns>The engine's counts, or <see langword="null"/> when the provider sent none.</returns>
    private static TokenUsage? ToUsage(OpenAiUsage? usage) =>
        usage is null ? null : new TokenUsage(usage.PromptTokens, usage.CompletionTokens);

    /// <summary>Builds the body to send. The caller's fields pass through unchanged.</summary>
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
    /// <returns>The chunks that carry something useful. The Application layer decides whether the caller gets the token counts.</returns>
    private async IAsyncEnumerable<ChatChunk> ReadChunksAsync(IAsyncEnumerable<SseItem<string>> events)
    {
        await foreach (var item in events.ConfigureAwait(false))
        {
            if (item.Data == StreamEnd)
            {
                yield break;
            }

            var chunk = Parse(item.Data);
            if (chunk is not null)
            {
                yield return chunk;
            }
        }
    }

    /// <summary>Parses one stream event.</summary>
    /// <param name="data">The event's JSON text.</param>
    /// <returns>A chunk, or <see langword="null"/> when the event carries nothing useful, such as a role-only delta.</returns>
    /// <exception cref="ChatStreamException">The event reports an error, or the engine cannot read it.</exception>
    private ChatChunk? Parse(string data)
    {
        OpenAiChatResponse? body;
        try
        {
            body = JsonSerializer.Deserialize(data, ProviderJson.Default.OpenAiChatResponse);
        }
        catch (JsonException exception)
        {
            throw new ChatStreamException(transport.Malformed(), exception);
        }

        if (body?.Error is not null)
        {
            throw new ChatStreamException(transport.StreamFailed());
        }

        var choice = body?.Choices is [var first, ..] ? first : null;
        var content = choice?.Delta?.Content;
        var finishReason = ToFinishReason(choice?.FinishReason);
        var usage = ToUsage(body?.Usage);
        return string.IsNullOrEmpty(content) && finishReason is null && usage is null
            ? null
            : new ChatChunk(string.IsNullOrEmpty(content) ? null : content, finishReason, usage);
    }
}

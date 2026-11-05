using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Polly.Timeout;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

/// <summary>Speaks the OpenAI chat completions protocol. Cloud services and the local llama-server share this code.</summary>
/// <param name="providerName">The provider's name, for error messages.</param>
/// <param name="httpClientFactory">Creates HTTP clients, so that the connection pool works correctly.</param>
/// <param name="httpClientName">The named client, which carries the provider's resilience settings.</param>
internal sealed class OpenAiClient(
    string providerName,
    IHttpClientFactory httpClientFactory,
    string httpClientName)
{
    /// <summary>The value a server sends as the last stream event.</summary>
    private const string StreamEnd = "[DONE]";

    /// <summary>Checks a request and adds the stored prompt as the first message, because model chat templates read it only from there.</summary>
    /// <param name="body">The caller's JSON. This method does not change it.</param>
    /// <param name="prompt">The stored prompt, or <see langword="null"/> when the provider has none.</param>
    /// <returns>The body to send, or an invalid-request failure.</returns>
    public static Result<JsonObject> ApplySystemPrompt(JsonObject body, string? prompt)
    {
        if (SystemPromptRules.FindProblem(body) is { } problem)
        {
            return Result<JsonObject>.Fail(problem);
        }

        if (prompt is null)
        {
            return Result<JsonObject>.Ok(body);
        }

        var result = body.DeepClone().AsObject();
        result["messages"]!.AsArray().Insert(0, new JsonObject { ["role"] = "system", ["content"] = prompt });
        return Result<JsonObject>.Ok(result);
    }

    /// <summary>Generates a complete answer.</summary>
    /// <param name="baseUrl">The server's base address, including any version path.</param>
    /// <param name="apiKey">The key for this server.</param>
    /// <param name="request">The validated request.</param>
    /// <param name="cancellationToken">Cancels generation.</param>
    /// <returns>The answer, or the expected failure.</returns>
    public async Task<Result<ChatCompletion>> CompleteAsync(Uri baseUrl, string apiKey, ChatRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(baseUrl, apiKey, ToWire(request, stream: false), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<ChatCompletion>.Fail(await HttpFailure.FromResponseAsync(providerName, response, cancellationToken).ConfigureAwait(false));
            }

            var body = await response.Content
                .ReadFromJsonAsync(ProviderJson.Default.OpenAiChatResponse, cancellationToken)
                .ConfigureAwait(false);
            var choice = body?.Choices is [var first, ..] ? first : null;
            if (choice?.Message?.Content is not { } content)
            {
                return Result<ChatCompletion>.Fail(HttpFailure.Malformed(providerName));
            }

            return Result<ChatCompletion>.Ok(new ChatCompletion(
                content,
                ToFinishReason(choice.FinishReason) ?? FinishReason.Stop,
                ToUsage(body!.Usage)));
        }
        catch (JsonException)
        {
            return Result<ChatCompletion>.Fail(HttpFailure.Malformed(providerName));
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            return Result<ChatCompletion>.Fail(HttpFailure.Unreachable(providerName));
        }
    }

    /// <summary>Starts a streamed answer.</summary>
    /// <param name="baseUrl">The server's base address, including any version path.</param>
    /// <param name="apiKey">The key for this server.</param>
    /// <param name="request">The validated request.</param>
    /// <param name="cancellationToken">Cancels generation.</param>
    /// <returns>The stream, or a failure if generation could not start.</returns>
    public async Task<Result<IAsyncEnumerable<ChatChunk>>> StreamAsync(Uri baseUrl, string apiKey, ChatRequest request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(baseUrl, apiKey, ToWire(request, stream: true), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            return Result<IAsyncEnumerable<ChatChunk>>.Fail(HttpFailure.Unreachable(providerName));
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                return Result<IAsyncEnumerable<ChatChunk>>.Fail(await HttpFailure.FromResponseAsync(providerName, response, cancellationToken).ConfigureAwait(false));
            }
        }

        return Result<IAsyncEnumerable<ChatChunk>>.Ok(ReadChunksAsync(response, WantsUsage(request), cancellationToken));
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
        var body = request.Body.DeepClone().AsObject();
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

    /// <summary>Tells whether the caller asked for token counts in a stream.</summary>
    /// <param name="request">The routed request.</param>
    /// <returns><see langword="true"/> when the caller set <c>stream_options.include_usage</c>.</returns>
    private static bool WantsUsage(ChatRequest request) =>
        request.Body["stream_options"] is JsonObject streamOptions
        && streamOptions["include_usage"] is JsonValue value
        && value.TryGetValue<bool>(out var includeUsage)
        && includeUsage;

    /// <summary>Sends the request and returns as soon as response headers arrive, so that streams are not buffered.</summary>
    /// <param name="baseUrl">The server's base address.</param>
    /// <param name="apiKey">The key for this server.</param>
    /// <param name="body">The request body.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, which the caller must dispose.</returns>
    private async Task<HttpResponseMessage> SendAsync(Uri baseUrl, string apiKey, JsonObject body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl.ToString().TrimEnd('/')}/chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var client = httpClientFactory.CreateClient(httpClientName);
        return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Converts stream events into chunks and releases the connection at the end.</summary>
    /// <param name="response">The successful response, which this method takes ownership of.</param>
    /// <param name="includeUsage">Whether the caller asked for token counts. Some servers send them anyway, and some clients cannot handle a piece without choices.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The chunks that carry something useful.</returns>
    private async IAsyncEnumerable<ChatChunk> ReadChunksAsync(HttpResponseMessage response, bool includeUsage, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in SseReader.ReadAsync(providerName, response, cancellationToken).ConfigureAwait(false))
        {
            if (item.Data == StreamEnd)
            {
                yield break;
            }

            var chunk = Parse(item.Data, includeUsage);
            if (chunk is not null)
            {
                yield return chunk;
            }
        }
    }

    /// <summary>Parses one stream event.</summary>
    /// <param name="data">The event's JSON text.</param>
    /// <param name="includeUsage">Whether to keep token counts.</param>
    /// <returns>A chunk, or <see langword="null"/> when the event carries nothing useful, such as a role-only delta.</returns>
    /// <exception cref="ChatStreamException">The event reports an error, or the engine cannot read it.</exception>
    private ChatChunk? Parse(string data, bool includeUsage)
    {
        OpenAiChatResponse? body;
        try
        {
            body = JsonSerializer.Deserialize(data, ProviderJson.Default.OpenAiChatResponse);
        }
        catch (JsonException exception)
        {
            throw new ChatStreamException(HttpFailure.Malformed(providerName), exception);
        }

        if (body?.Error is not null)
        {
            throw new ChatStreamException(new EngineError(ErrorKind.Upstream, $"The provider '{providerName}' failed during the stream."));
        }

        var choice = body?.Choices is [var first, ..] ? first : null;
        var content = choice?.Delta?.Content;
        var finishReason = ToFinishReason(choice?.FinishReason);
        var usage = includeUsage ? ToUsage(body?.Usage) : null;
        return string.IsNullOrEmpty(content) && finishReason is null && usage is null
            ? null
            : new ChatChunk(string.IsNullOrEmpty(content) ? null : content, finishReason, usage);
    }
}

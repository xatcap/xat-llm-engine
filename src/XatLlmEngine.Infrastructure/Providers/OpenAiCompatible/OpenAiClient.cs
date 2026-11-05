using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Polly.Timeout;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

/// <summary>Speaks the OpenAI chat completions protocol. Cloud services and the local llama-server share this code.</summary>
/// <param name="providerName">The provider's name, for error messages.</param>
/// <param name="httpClientFactory">Creates HTTP clients, so that connections are pooled properly.</param>
/// <param name="httpClientName">The named client, which carries the provider's resilience settings.</param>
/// <param name="useMaxCompletionTokens">Whether to send the newer token limit field.</param>
internal sealed class OpenAiClient(
    string providerName,
    IHttpClientFactory httpClientFactory,
    string httpClientName,
    bool useMaxCompletionTokens)
{
    /// <summary>The value a server sends as the last stream event.</summary>
    private const string StreamEnd = "[DONE]";

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

        return Result<IAsyncEnumerable<ChatChunk>>.Ok(ReadChunksAsync(response, cancellationToken));
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
    /// <returns>The engine's counts, or <see langword="null"/> when none were sent.</returns>
    private static TokenUsage? ToUsage(OpenAiUsage? usage) =>
        usage is null ? null : new TokenUsage(usage.PromptTokens, usage.CompletionTokens);

    /// <summary>Converts a message role to its wire value.</summary>
    /// <param name="role">The role.</param>
    /// <returns>The wire value.</returns>
    private static string ToWire(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.Assistant => "assistant",
        _ => "user",
    };

    /// <summary>Sends the request and returns as soon as response headers arrive, so that streams are not buffered.</summary>
    /// <param name="baseUrl">The server's base address.</param>
    /// <param name="apiKey">The key for this server.</param>
    /// <param name="body">The request body.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, which the caller must dispose.</returns>
    private async Task<HttpResponseMessage> SendAsync(Uri baseUrl, string apiKey, OpenAiChatRequest body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl.ToString().TrimEnd('/')}/chat/completions"))
        {
            Content = JsonContent.Create(body, ProviderJson.Default.OpenAiChatRequest),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var client = httpClientFactory.CreateClient(httpClientName);
        return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the request body.</summary>
    /// <param name="request">The validated request.</param>
    /// <param name="stream">Whether to ask for a stream.</param>
    /// <returns>The request body.</returns>
    private OpenAiChatRequest ToWire(ChatRequest request, bool stream) => new(
        request.Model,
        [.. request.Messages.Select(message => new OpenAiMessage(ToWire(message.Role), message.Content))],
        request.Temperature,
        request.TopP,
        useMaxCompletionTokens ? null : request.MaxTokens,
        useMaxCompletionTokens ? request.MaxTokens : null,
        request.Stop.Count > 0 ? request.Stop : null,
        stream ? true : null,
        stream ? new OpenAiStreamOptions(IncludeUsage: true) : null);

    /// <summary>Converts stream events into chunks and releases the connection at the end.</summary>
    /// <param name="response">The successful response, which this method takes ownership of.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The chunks that carry something useful.</returns>
    private async IAsyncEnumerable<ChatChunk> ReadChunksAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in SseReader.ReadAsync(providerName, response, cancellationToken).ConfigureAwait(false))
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
    /// <exception cref="ChatStreamException">The event reports an error or cannot be read.</exception>
    private ChatChunk? Parse(string data)
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
            throw new ChatStreamException(new EngineError(ErrorKind.Upstream, $"The provider '{providerName}' failed while streaming."));
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

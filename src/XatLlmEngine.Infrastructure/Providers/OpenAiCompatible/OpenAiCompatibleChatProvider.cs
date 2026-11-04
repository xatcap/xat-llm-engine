using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

/// <summary>A cloud provider that speaks the OpenAI protocol, such as OpenAI itself or another compatible service.</summary>
/// <param name="name">The provider's name.</param>
/// <param name="options">The provider's settings. Its base URL and key must be set.</param>
/// <param name="client">The protocol client.</param>
internal sealed class OpenAiCompatibleChatProvider(string name, ProviderOptions options, OpenAiClient client) : IChatProvider
{
    /// <inheritdoc/>
    public string Name => name;

    /// <inheritdoc/>
    public IReadOnlyList<string> Models => options.Models;

    /// <inheritdoc/>
    public Task<Result<ChatCompletion>> CompleteAsync(ChatRequest request, CancellationToken cancellationToken) =>
        client.CompleteAsync(options.BaseUrl!, options.ApiKey!, request, cancellationToken);

    /// <inheritdoc/>
    public Task<Result<IAsyncEnumerable<ChatChunk>>> StreamAsync(ChatRequest request, CancellationToken cancellationToken) =>
        client.StreamAsync(options.BaseUrl!, options.ApiKey!, request, cancellationToken);
}

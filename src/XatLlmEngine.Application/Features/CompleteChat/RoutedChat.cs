using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Features.CompleteChat;

/// <summary>A validated chat request paired with the provider that will serve it.</summary>
/// <param name="Provider">The selected provider.</param>
/// <param name="Request">The validated request.</param>
/// <param name="Model">The full <c>provider/model</c> reference to report back to the caller.</param>
internal sealed record RoutedChat(IChatProvider Provider, ChatRequest Request, ModelReference Model);

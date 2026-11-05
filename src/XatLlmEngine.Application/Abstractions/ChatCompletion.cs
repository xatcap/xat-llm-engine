using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Abstractions;

/// <summary>A complete answer from a provider.</summary>
/// <param name="Content">The generated text.</param>
/// <param name="FinishReason">Why generation stopped.</param>
/// <param name="Usage">Token counts, or <see langword="null"/> when the provider did not report them.</param>
public sealed record ChatCompletion(string Content, FinishReason FinishReason, TokenUsage? Usage);

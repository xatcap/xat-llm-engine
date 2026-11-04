using XatLlmEngine.Domain;

namespace XatLlmEngine.Application.Abstractions;

/// <summary>One piece of a streamed answer. A piece can carry any combination of its values.</summary>
/// <param name="Content">New text, or <see langword="null"/> when this piece has none.</param>
/// <param name="FinishReason">Why generation stopped, or <see langword="null"/> when it has not stopped.</param>
/// <param name="Usage">Token counts, or <see langword="null"/> when this piece has none.</param>
public sealed record ChatChunk(string? Content, FinishReason? FinishReason, TokenUsage? Usage);

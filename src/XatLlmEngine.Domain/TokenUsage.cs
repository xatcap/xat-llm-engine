namespace XatLlmEngine.Domain;

/// <summary>How many tokens a request used.</summary>
/// <param name="InputTokens">Tokens in the prompt.</param>
/// <param name="OutputTokens">Tokens the model generated.</param>
public sealed record TokenUsage(int InputTokens, int OutputTokens)
{
    /// <summary>Gets the sum of the input and output tokens.</summary>
    public int TotalTokens => InputTokens + OutputTokens;
}

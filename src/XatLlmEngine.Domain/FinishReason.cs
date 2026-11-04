namespace XatLlmEngine.Domain;

/// <summary>Why the model stopped generating.</summary>
public enum FinishReason
{
    /// <summary>The model finished naturally or reached a stop sequence.</summary>
    Stop,

    /// <summary>The token limit cut the answer short.</summary>
    Length,

    /// <summary>The provider's safety system stopped or refused the answer.</summary>
    ContentFilter,
}

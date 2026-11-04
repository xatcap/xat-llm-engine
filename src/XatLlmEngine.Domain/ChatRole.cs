namespace XatLlmEngine.Domain;

/// <summary>Who wrote a chat message.</summary>
public enum ChatRole
{
    /// <summary>Instructions that shape how the model behaves.</summary>
    System,

    /// <summary>A message from the person or program using the model.</summary>
    User,

    /// <summary>A previous reply from the model.</summary>
    Assistant,
}

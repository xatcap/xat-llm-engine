namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Names the local model's entry in the health report, so that the host can find it.</summary>
public static class LocalModelHealth
{
    /// <summary>The name of the health check entry. Its description is the stage of the local model, in lowercase, for example <c>ready</c>.</summary>
    public const string EntryName = "local-model";
}

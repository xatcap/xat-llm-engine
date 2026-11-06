namespace XatLlmEngine.Infrastructure;

/// <summary>Settings of the engine process.</summary>
public sealed class EngineOptions
{
    /// <summary>The configuration section that holds these settings.</summary>
    public const string SectionName = "Engine";

    /// <summary>
    /// Gets the folder for the engine's own files: the instance lock, the PID file of llama-server and the usage counts.
    /// Leave it empty to use a folder for this install in the user's private data folder. A relative path starts at the engine's folder.
    /// </summary>
    public string DataFolder { get; init; } = string.Empty;
}

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

    /// <summary>
    /// Gets the path of the ready file. When the API starts, the engine writes its address, its process ID and its version to this file.
    /// The engine deletes the file when it stops. Leave it empty to write no file. A relative path starts at the engine's folder.
    /// </summary>
    public string ReadyFile { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the engine stops when its standard input closes.
    /// A parent app that keeps the input open can then stop the engine on every system, also when the app itself stops unexpectedly.
    /// </summary>
    public bool StopWhenInputCloses { get; init; }
}

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Resolves the configured file locations.</summary>
internal static class LlamaServerPaths
{
    /// <summary>Resolves a path from the engine's folder, because the current folder depends on how the engine was started.</summary>
    /// <param name="path">An absolute path, or a path relative to the engine.</param>
    /// <returns>An absolute path.</returns>
    public static string FromEngineFolder(string path) => Path.GetFullPath(path, AppContext.BaseDirectory);

    /// <summary>Resolves the llama-server program.</summary>
    /// <param name="options">The local model settings.</param>
    /// <returns>The absolute path, with the <c>.exe</c> suffix on Windows.</returns>
    public static string ServerProgram(LlamaCppOptions options)
    {
        var path = FromEngineFolder(options.ServerPath);
        return OperatingSystem.IsWindows() && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? $"{path}.exe" : path;
    }
}

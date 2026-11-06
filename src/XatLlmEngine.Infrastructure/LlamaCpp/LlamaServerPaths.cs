namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Resolves the configured file locations.</summary>
internal static class LlamaServerPaths
{
    /// <summary>Resolves a path from the engine's folder, because the current folder depends on how the engine started.</summary>
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

    /// <summary>Gives the llama.cpp config files that llama-server reads, with the same rules as llama.cpp b12110.</summary>
    /// <remarks>
    /// On Windows: <c>%PROGRAMDATA%\llama.cpp\config.ini</c> and <c>%APPDATA%\llama.cpp\config.ini</c>.
    /// On other systems: <c>/etc/llama.cpp/config.ini</c>, and <c>config.ini</c> in <c>$XDG_CONFIG_HOME/llama.cpp</c>.
    /// When <c>XDG_CONFIG_HOME</c> is not set, the folder is <c>$HOME/.config/llama.cpp</c>.
    /// </remarks>
    /// <param name="isWindows">Whether to use the Windows rules.</param>
    /// <param name="variable">Reads an environment variable. An empty value counts as not set.</param>
    /// <returns>The paths. A file at a path may not exist.</returns>
    public static IReadOnlyList<string> ConfigFiles(bool isWindows, Func<string, string?> variable)
    {
        string? Read(string name) => variable(name) is { Length: > 0 } value ? value : null;

        var folders = new List<string>();
        if (isWindows)
        {
            folders.AddRange(new[] { Read("PROGRAMDATA"), Read("APPDATA") }.OfType<string>());
        }
        else
        {
            folders.Add("/etc");
            folders.Add(Read("XDG_CONFIG_HOME")
                ?? Path.Combine(Read("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        }

        return [.. folders.Select(folder => Path.Combine(folder, "llama.cpp", "config.ini"))];
    }
}

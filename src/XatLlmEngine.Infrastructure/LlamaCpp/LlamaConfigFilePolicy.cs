namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Finds a llama.cpp config file, which the engine does not allow.</summary>
/// <remarks>
/// llama-server always reads these files, and no flag or variable turns this off. A file can turn on options that
/// <see cref="LlamaCppOptions.ExtraArguments"/> does not allow, such as a log file. The engine cannot replace all these
/// values safely from the command line, so the local model fails when a file exists. Cloud providers continue to work.
/// </remarks>
internal static class LlamaConfigFilePolicy
{
    /// <summary>Finds the first config file that llama-server would read.</summary>
    /// <param name="program">The llama-server path. llama-server resolves a relative folder from its own working folder, which is this file's folder.</param>
    /// <returns>The path of the file, or <see langword="null"/> when no file exists.</returns>
    public static string? FindConfigFile(string program)
    {
        var folder = Path.GetDirectoryName(program)!;
        return LlamaServerPaths.ConfigFiles(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable)
            .Select(path => Path.GetFullPath(path, folder))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Builds the failure for a config file.</summary>
    /// <param name="configFile">The path of the file.</param>
    /// <returns>The failure.</returns>
    public static LlamaFailure Failure(string configFile) => new(
        "A llama.cpp config file can change llama-server settings, and the engine cannot turn it off. Remove or rename the file, then restart the engine. The engine log shows its path.",
        $"The llama.cpp config file '{configFile}' can change llama-server settings. Remove or rename the file, then restart the engine.");
}

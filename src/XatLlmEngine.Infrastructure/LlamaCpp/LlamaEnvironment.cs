using System.Diagnostics;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Prepares the environment of llama-server.</summary>
internal static class LlamaEnvironment
{
    /// <summary>The prefixes of the llama.cpp variables. See <see cref="IsLlamaSetting"/>.</summary>
    private static readonly string[] LlamaNameSpaces = ["LLAMA_", "LLAMACPP_", "GGML_", "MTMD_", "AIP_", "HF_", "LLGUIDANCE_"];

    /// <summary>The llama.cpp variables outside the name spaces. See <see cref="IsLlamaSetting"/>.</summary>
    private static readonly string[] LlamaNames = ["MODEL_ENDPOINT", "HTTP_PROXY", "HTTPS_PROXY"];

    /// <summary>Removes the engine's secrets and the llama.cpp settings, then adds the key for this run.</summary>
    /// <remarks>
    /// A child process inherits the environment. llama-server has no need for the engine's own secrets.
    /// llama-server also reads its own variables, and these can turn on options that <see cref="LlamaCppOptions.ExtraArguments"/> does not allow.
    /// </remarks>
    /// <param name="startInfo">The process settings.</param>
    /// <param name="programFolder">The folder of llama-server, which holds its libraries.</param>
    /// <param name="apiKey">The key for this run. It goes through the environment, because other users on the machine can see the arguments.</param>
    public static void Prepare(ProcessStartInfo startInfo, string programFolder, string apiKey)
    {
        foreach (var name in startInfo.Environment.Keys.Where(name => IsEngineSecret(name) || IsLlamaSetting(name)).ToList())
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment["LLAMA_API_KEY"] = apiKey;
        if (OperatingSystem.IsLinux())
        {
            // Existing entries stay, because GPU drivers can depend on them.
            var existing = startInfo.Environment.TryGetValue("LD_LIBRARY_PATH", out var value) ? value : null;
            startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? programFolder : $"{programFolder}:{existing}";
        }
    }

    /// <summary>Tells whether an environment variable holds settings for the engine's cloud providers.</summary>
    /// <param name="name">The variable's name.</param>
    /// <returns><see langword="true"/> when the variable must not reach llama-server.</returns>
    internal static bool IsEngineSecret(string name) =>
        name.StartsWith("Providers__", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Providers:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tells whether an environment variable holds a llama.cpp setting.</summary>
    /// <remarks>
    /// A scan of all the llama.cpp source that the packages contain, at the tag in <see cref="LlamaCppCompatibility.VerifiedTag"/>, found these variables:
    /// <list type="bullet">
    /// <item>names in the llama.cpp name spaces <c>LLAMA_</c>, <c>LLAMACPP_</c>, <c>GGML_</c>, <c>MTMD_</c>, <c>AIP_</c>, <c>HF_</c> and <c>LLGUIDANCE_</c>;</item>
    /// <item><c>MODEL_ENDPOINT</c>, <c>HTTP_PROXY</c> and <c>HTTPS_PROXY</c>, which only downloads use.</item>
    /// </list>
    /// Some of them change the port, load a library, write files or print internal data. Later versions use the same name spaces, so this rule also removes their new names.
    /// The rule keeps the variables of the operating system and the GPU drivers, such as <c>PATH</c>, <c>HOME</c>, <c>XDG_*</c>, <c>TEMP</c> and <c>VK_*</c>. llama-server and the drivers need them.
    /// </remarks>
    /// <param name="name">The variable's name.</param>
    /// <returns><see langword="true"/> when the variable must not reach llama-server.</returns>
    internal static bool IsLlamaSetting(string name) =>
        LlamaNameSpaces.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || LlamaNames.Contains(name, StringComparer.OrdinalIgnoreCase);
}

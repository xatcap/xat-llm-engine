using System.Globalization;
using System.Net;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Builds the llama-server arguments.</summary>
internal static class LlamaServerArguments
{
    /// <summary>Builds the arguments. The API key is not here, because arguments are visible to every user on the machine.</summary>
    /// <remarks>
    /// The context size, GPU layers and parallel slots come from the settings, unless <see cref="LlamaCppOptions.ExtraArguments"/> sets them.
    /// Thus llama-server never gets the same flag two times. The engine does not set <c>--threads-http</c>: in the tag of
    /// <see cref="LlamaCppCompatibility.VerifiedTag"/>, llama-server keeps at least four more HTTP threads than slots, plus threads
    /// that it adds when it needs them. The engine sends at most one request per slot, so the health checks always get a thread.
    /// </remarks>
    /// <param name="options">The local model settings.</param>
    /// <param name="port">The port to listen on.</param>
    /// <returns>The arguments in order.</returns>
    public static IReadOnlyList<string> Build(LlamaCppOptions options, int port) =>
    [
        "--model", LlamaServerPaths.FromEngineFolder(options.ModelPath),
        "--host", IPAddress.Loopback.ToString(),
        "--port", port.ToString(CultureInfo.InvariantCulture),
        .. options.HasExtraFlag("-c", "--ctx-size") ? [] : new[] { "--ctx-size", options.ContextSize.ToString(CultureInfo.InvariantCulture) },
        .. options.HasExtraFlag("-ngl", "--gpu-layers", "--n-gpu-layers") ? [] : new[] { "--gpu-layers", options.GpuLayers },
        .. options.HasExtraFlag("-np", "--parallel") ? [] : new[] { "--parallel", options.Parallel.ToString(CultureInfo.InvariantCulture) },
        "--no-webui",
        .. options.ExtraArguments,
    ];
}

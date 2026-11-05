using System.ComponentModel.DataAnnotations;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Settings for the local model and the llama-server program that runs it.</summary>
public sealed class LlamaCppOptions
{
    /// <summary>The configuration section that holds these settings.</summary>
    public const string SectionName = "LocalModel";

    /// <summary>Gets a value indicating whether the <c>local</c> provider is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets the name callers use after the <c>local/</c> prefix.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ModelName { get; init; } = "ministral-3-3b";

    /// <summary>Gets the model file path. A relative path starts at the engine's folder.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ModelPath { get; init; } = "models/Ministral-3-3B-Instruct-2512-Q4_K_M.gguf";

    /// <summary>Gets the llama-server path without any <c>.exe</c> suffix, which is added on Windows. A relative path starts at the engine's folder.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ServerPath { get; init; } = "llama/llama-server";

    /// <summary>
    /// Gets the context size in tokens, shared across <see cref="Parallel"/> slots. It is always set explicitly because
    /// the server would otherwise use the model's maximum of 256k tokens, which needs far more memory than the model file.
    /// </summary>
    [Range(256, 1_048_576)]
    public int ContextSize { get; init; } = 8192;

    /// <summary>Gets how many layers to place on the GPU: a number, <c>auto</c> or <c>all</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string GpuLayers { get; init; } = "auto";

    /// <summary>Gets how many requests can generate at once. Others wait in llama-server's queue.</summary>
    [Range(1, 256)]
    public int Parallel { get; init; } = 1;

    /// <summary>Gets the temperature to use when a caller sends none. Mistral recommends values below 0.1 for everyday use.</summary>
    [Range(0.0, 2.0)]
    public double DefaultTemperature { get; init; } = 0.07;

    /// <summary>Gets how long a model may take to load, in seconds, before the server is restarted.</summary>
    [Range(1, 86_400)]
    public int StartupTimeoutSeconds { get; init; } = 600;

    /// <summary>Gets the seconds between health checks of a running server. Three failures in a row cause a restart, because a hung process never exits by itself.</summary>
    [Range(1, 3_600)]
    public int HealthCheckIntervalSeconds { get; init; } = 10;

    /// <summary>Gets more llama-server arguments, for example <c>--threads 4</c> split into two values.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>Gets the request fields the local model accepts, besides <c>model</c> and <c>stream</c>. This list is its request template.</summary>
    [MinLength(1, ErrorMessage = "RequestFields must list at least one field.")]
    public IReadOnlyList<string> RequestFields { get; init; } = [];
}

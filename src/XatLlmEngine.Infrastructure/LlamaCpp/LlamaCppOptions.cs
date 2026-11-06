using System.Collections.Frozen;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Settings for the local model and the llama-server program that runs it.</summary>
public sealed class LlamaCppOptions : IValidatableObject
{
    /// <summary>
    /// The llama-server flags that <see cref="ExtraArguments"/> can contain. Every other flag stops the engine at startup.
    /// The value tells whether the flag takes a value in the next entry.
    /// </summary>
    /// <remarks>
    /// The list holds only tuning flags. It does not hold flags that take a file path, a URL or a network address.
    /// It also does not hold flags that print or write logs.
    /// Thus the new flags of a later llama.cpp version stop the engine, until a person examines them and adds them here.
    /// The list is for the llama.cpp tag in <see cref="LlamaCppCompatibility.VerifiedTag"/>. After an upgrade, do these checks:
    /// <list type="bullet">
    /// <item>Make sure that each flag still exists. llama-server does not start with an unknown flag.</item>
    /// <item>Make sure that each flag still takes a value, or no value, as this list tells.</item>
    /// </list>
    /// </remarks>
    internal static readonly FrozenDictionary<string, bool> AllowedFlags = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        // CPU threads.
        ["-t"] = true, ["--threads"] = true, ["-tb"] = true, ["--threads-batch"] = true,
        ["--prio"] = true, ["--poll"] = true, ["--numa"] = true, ["--threads-http"] = true,

        // Batches, memory and context.
        ["-b"] = true, ["--batch-size"] = true, ["-ub"] = true, ["--ubatch-size"] = true, ["-cram"] = true, ["--cache-ram"] = true,
        ["-ctk"] = true, ["--cache-type-k"] = true, ["-ctv"] = true, ["--cache-type-v"] = true, ["-dt"] = true, ["--defrag-thold"] = true,
        ["-lm"] = true, ["--load-mode"] = true, ["--keep"] = true, ["--cache-reuse"] = true,
        ["-kvu"] = false, ["--kv-unified"] = false, ["-no-kvu"] = false, ["--no-kv-unified"] = false, ["--swa-full"] = false,
        ["--context-shift"] = false, ["--no-context-shift"] = false,

        // GPU.
        ["-fa"] = true, ["--flash-attn"] = true, ["-fit"] = true, ["--fit"] = true, ["-dev"] = true, ["--device"] = true,
        ["-sm"] = true, ["--split-mode"] = true, ["-ts"] = true, ["--tensor-split"] = true, ["-mg"] = true, ["--main-gpu"] = true,
        ["-ncmoe"] = true, ["--n-cpu-moe"] = true, ["-ot"] = true, ["--override-tensor"] = true,
        ["-cmoe"] = false, ["--cpu-moe"] = false, ["--op-offload"] = false, ["--no-op-offload"] = false,
        ["--repack"] = false, ["-nr"] = false, ["--no-repack"] = false,

        // Server.
        ["-to"] = true, ["--timeout"] = true, ["-n"] = true, ["--predict"] = true, ["--n-predict"] = true,
        ["-cb"] = false, ["--cont-batching"] = false, ["-nocb"] = false, ["--no-cont-batching"] = false,
        ["--warmup"] = false, ["--no-warmup"] = false,

        // Chat format.
        ["--reasoning-format"] = true, ["-rea"] = true, ["--reasoning"] = true, ["--reasoning-budget"] = true,
        ["--jinja"] = false, ["--no-jinja"] = false,

        // Flags that replace the ContextSize, GpuLayers or Parallel setting. Validate also checks the range of the context size and the parallel slots.
        ["-c"] = true, ["--ctx-size"] = true, ["-ngl"] = true, ["--gpu-layers"] = true, ["--n-gpu-layers"] = true, ["-np"] = true, ["--parallel"] = true,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The smallest context size.</summary>
    private const int MinContextSize = 256;

    /// <summary>The largest context size.</summary>
    private const int MaxContextSize = 1_048_576;

    /// <summary>The smallest number of parallel slots.</summary>
    private const int MinParallel = 1;

    /// <summary>The largest number of parallel slots.</summary>
    private const int MaxParallel = 256;

    /// <summary>The configuration section that holds these settings.</summary>
    public const string SectionName = "LocalModel";

    /// <summary>Gets a value that tells whether the <c>local</c> provider is on.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets the name callers use after the <c>local/</c> prefix.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ModelName { get; init; } = "ministral-3-3b";

    /// <summary>Gets the model file path. A relative path starts at the engine's folder.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ModelPath { get; init; } = "models/Ministral-3-3B-Instruct-2512-Q4_K_M.gguf";

    /// <summary>Gets the llama-server path without a <c>.exe</c> suffix. The engine adds this suffix on Windows. A relative path starts at the engine's folder.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ServerPath { get; init; } = "llama/llama-server";

    /// <summary>
    /// Gets the context size in tokens, shared across <see cref="Parallel"/> slots. It is always set explicitly because
    /// the server would otherwise use the model's maximum of 256k tokens, which needs far more memory than the model file.
    /// <c>--ctx-size</c> in <see cref="ExtraArguments"/> replaces this value.
    /// </summary>
    [Range(MinContextSize, MaxContextSize)]
    public int ContextSize { get; init; } = 8192;

    /// <summary>Gets how many layers to place on the GPU: a number, <c>auto</c> or <c>all</c>. <c>--gpu-layers</c> in <see cref="ExtraArguments"/> replaces this value.</summary>
    [Required(AllowEmptyStrings = false)]
    public string GpuLayers { get; init; } = "auto";

    /// <summary>Gets how many requests can generate at once. The engine sends no more than this number to llama-server. Other requests wait in the engine's queue. <c>--parallel</c> in <see cref="ExtraArguments"/> replaces this value.</summary>
    [Range(MinParallel, MaxParallel)]
    public int Parallel { get; init; } = 1;

    /// <summary>Gets the temperature to use when a caller sends none. Mistral recommends values below 0.1 for everyday use.</summary>
    [Range(0.0, 2.0)]
    public double DefaultTemperature { get; init; } = 0.07;

    /// <summary>Gets how long a model can take to load, in seconds. When the time ends, the local model fails, and the engine does not start llama-server again until the engine restarts.</summary>
    [Range(1, 86_400)]
    public int StartupTimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// Gets the seconds between health checks of a running server. Three failed checks in a row while no request runs cause a restart,
    /// because a process that stops responding never exits by itself. While requests run, failed checks do not stop the server. See <see cref="UnresponsiveTimeoutSeconds"/>.
    /// </summary>
    [Range(1, 3_600)]
    public int HealthCheckIntervalSeconds { get; init; } = 10;

    /// <summary>Gets the largest number of output tokens that a caller can ask for, or 0 for no limit. A request with no token field gets this limit.</summary>
    [Range(0, int.MaxValue)]
    public int MaxTokensLimit { get; init; }

    /// <summary>Gets how many requests can wait for a free slot. A request that finds the queue full gets status 503 at once. Use 0 to refuse every request that finds no free slot.</summary>
    [Range(0, 10_000)]
    public int MaxQueuedRequests { get; init; } = 32;

    /// <summary>
    /// Gets how long a busy server can fail its health checks and also send no data, in seconds. After this time, the engine stops the
    /// server and starts it again, and the requests in progress fail. A server that sends data is never stopped.
    /// </summary>
    [Range(30, 86_400)]
    public int UnresponsiveTimeoutSeconds { get; init; } = 300;

    /// <summary>Gets more llama-server arguments, for example <c>--threads 4</c> split into two values. The engine allows only tuning flags.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>Gets the request fields the local model accepts, besides <c>model</c> and <c>stream</c>. This list is its request template.</summary>
    [MinLength(1, ErrorMessage = "RequestFields must list at least one field.")]
    public IReadOnlyList<string> RequestFields { get; init; } = [];

    /// <summary>Writes an argument in the form that llama-server uses to find a flag.</summary>
    /// <param name="argument">One entry of <see cref="ExtraArguments"/>.</param>
    /// <returns>The argument. In a flag that starts with <c>--</c>, each <c>_</c> becomes <c>-</c>, as llama-server does.</returns>
    internal static string NormalizeFlag(string? argument)
    {
        var flag = argument ?? string.Empty;
        return flag.StartsWith("--", StringComparison.Ordinal) ? flag.Replace('_', '-') : flag;
    }

    /// <summary>Tells whether <see cref="ExtraArguments"/> sets a flag. A value never counts as a flag.</summary>
    /// <param name="aliases">All the names of the flag.</param>
    /// <returns><see langword="true"/> when a flag in the entries is one of the names.</returns>
    internal bool HasExtraFlag(params ReadOnlySpan<string> aliases)
    {
        foreach (var entry in ReadExtraArguments())
        {
            if (entry.Flag is not null && aliases.Contains(entry.Flag))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets the number of slots that llama-server really uses: the last value of <c>--parallel</c> in <see cref="ExtraArguments"/>, or else <see cref="Parallel"/>.</summary>
    internal int EffectiveParallel =>
        ReadExtraArguments()
            .Where(entry => entry is { Problem: null, Flag: "-np" or "--parallel" })
            .Select(entry => int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slots) ? slots : (int?)null)
            .LastOrDefault(slots => slots is >= MinParallel and <= MaxParallel)
        ?? Parallel;

    /// <inheritdoc/>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxTokensLimit > 0 && !RequestFields.Contains("max_tokens", StringComparer.Ordinal) && !RequestFields.Contains("max_completion_tokens", StringComparer.Ordinal))
        {
            yield return new ValidationResult("MaxTokensLimit needs max_tokens or max_completion_tokens in RequestFields.", [nameof(MaxTokensLimit)]);
        }

        foreach (var entry in ReadExtraArguments())
        {
            if (entry.Problem is not null)
            {
                yield return Failure(entry.Problem);
            }
            else if (entry.Flag is "-c" or "--ctx-size" && !IsInRange(entry.Value, MinContextSize, MaxContextSize))
            {
                // These values replace settings that have a range, so the same range applies.
                yield return Failure($"The value after '{entry.Argument}' in ExtraArguments must be a number from {MinContextSize} to {MaxContextSize}.");
            }
            else if (entry.Flag is "-np" or "--parallel" && !IsInRange(entry.Value, MinParallel, MaxParallel))
            {
                yield return Failure($"The value after '{entry.Argument}' in ExtraArguments must be a number from {MinParallel} to {MaxParallel}.");
            }
        }
    }

    /// <summary>Reads <see cref="ExtraArguments"/> from left to right, as llama-server does. A flag that takes a value uses the next entry as its value.</summary>
    /// <remarks>
    /// <see cref="Validate"/> and <see cref="HasExtraFlag"/> both use this method, so they always agree about which entries are flags.
    /// After a flag that is not allowed, the next entry counts as its value, unless that entry starts with '-'.
    /// </remarks>
    /// <returns>The flags with their values, and the problems.</returns>
    private IEnumerable<ExtraArgument> ReadExtraArguments()
    {
        for (var index = 0; index < ExtraArguments.Count; index++)
        {
            var argument = ExtraArguments[index];
            if (HasSpaces(argument))
            {
                yield return new(argument, null, null, $"Remove the empty entry or the spaces in '{argument}' in ExtraArguments. llama-server does not accept them.");
                continue;
            }

            // llama-server reads a flag only from an entry that starts with '-'.
            var flag = NormalizeFlag(argument);
            if (!flag.StartsWith('-'))
            {
                yield return new(argument, null, null, $"Remove '{argument}' from ExtraArguments, or put a flag before it. A value must follow its flag.");
                continue;
            }

            if (!AllowedFlags.TryGetValue(flag, out var takesValue))
            {
                // The next entry is probably the value of this flag, so it does not get a second message. An entry that starts with '-' is read as a flag.
                var nextIsValue = index + 1 < ExtraArguments.Count && !NormalizeFlag(ExtraArguments[index + 1]).StartsWith('-');
                index += nextIsValue ? 1 : 0;

                var hint = argument.Contains('=', StringComparison.Ordinal) ? " Write a flag and its value as two entries." : string.Empty;
                yield return new(argument, flag, null, $"Remove '{argument}' from ExtraArguments. The engine allows only tuning flags.{hint}");
                continue;
            }

            if (!takesValue)
            {
                yield return new(argument, flag, null, null);
                continue;
            }

            if (index + 1 >= ExtraArguments.Count)
            {
                yield return new(argument, flag, null, $"Add a value after '{argument}' in ExtraArguments.");
                continue;
            }

            var value = ExtraArguments[++index];
            if (HasSpaces(value))
            {
                yield return new(argument, flag, value, $"Remove the empty entry or the spaces in '{value}' in ExtraArguments. llama-server does not accept them.");
            }
            else if (value.StartsWith('-') && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                // No allowed flag needs such a value. This rule also stops a flag such as '--log-file' if this list is wrong about a flag.
                yield return new(argument, flag, value, $"The value '{value}' after '{argument}' in ExtraArguments cannot start with '-', unless it is a number.");
            }
            else
            {
                yield return new(argument, flag, value, null);
            }
        }
    }

    /// <summary>Tells whether an entry is empty or has spaces at the ends.</summary>
    /// <param name="argument">The entry.</param>
    /// <returns><see langword="true"/> when llama-server cannot use the entry.</returns>
    private static bool HasSpaces([NotNullWhen(false)] string? argument) =>
        string.IsNullOrWhiteSpace(argument) || argument != argument.Trim();

    /// <summary>Tells whether a value is a whole number in a range.</summary>
    /// <param name="value">The value, or <see langword="null"/> when there is no value.</param>
    /// <param name="minimum">The smallest allowed number.</param>
    /// <param name="maximum">The largest allowed number.</param>
    /// <returns><see langword="true"/> when the value is in the range.</returns>
    private static bool IsInRange(string? value, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= minimum && number <= maximum;

    /// <summary>Builds a validation failure for <see cref="ExtraArguments"/>.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The failure.</returns>
    private static ValidationResult Failure(string message) => new(message, [nameof(ExtraArguments)]);

    /// <summary>One flag of <see cref="ExtraArguments"/>, with its value, or a problem.</summary>
    /// <param name="Argument">The entry as the user wrote it.</param>
    /// <param name="Flag">The flag in the form that llama-server uses, or <see langword="null"/> when the entry is not a flag.</param>
    /// <param name="Value">The value after the flag, if there is one.</param>
    /// <param name="Problem">The message for a problem, or <see langword="null"/> when the entry is correct.</param>
    private sealed record ExtraArgument(string? Argument, string? Flag, string? Value, string? Problem);
}

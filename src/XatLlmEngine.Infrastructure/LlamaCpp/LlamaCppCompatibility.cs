namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>The llama.cpp build that the engine's rules were checked against.</summary>
/// <remarks>
/// These rules depend on that build: the flag list in <see cref="LlamaCppOptions.AllowedFlags"/>, the variables in
/// <see cref="LlamaEnvironment.IsLlamaSetting"/>, the config files in <see cref="LlamaServerPaths.ConfigFiles"/>, and the
/// routes <c>/health</c> and <c>/props</c>. A test fails when <c>build/llama-cpp.lock.json</c> names another tag.
/// To upgrade, check each rule against the new source, and then change this tag.
/// </remarks>
internal static class LlamaCppCompatibility
{
    /// <summary>The llama.cpp tag that the rules were checked against.</summary>
    public const string VerifiedTag = "b12110";
}

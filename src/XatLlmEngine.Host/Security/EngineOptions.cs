namespace XatLlmEngine.Host.Security;

/// <summary>Settings for the engine's own API.</summary>
public sealed class EngineOptions
{
    /// <summary>The configuration section that holds these settings.</summary>
    public const string SectionName = "Engine";

    /// <summary>Gets the keys callers can use. Blank values are ignored, so an unset variable cannot turn into an accepted key.</summary>
    public IReadOnlyList<string> ApiKeys { get; init; } = [];

    /// <summary>Gets the usable keys.</summary>
    internal IReadOnlyList<string> EffectiveKeys => [.. ApiKeys.Where(key => !string.IsNullOrWhiteSpace(key))];
}

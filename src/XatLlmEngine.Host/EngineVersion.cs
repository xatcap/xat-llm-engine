using System.Reflection;

namespace XatLlmEngine.Host;

/// <summary>The engine version. The health routes and the ready file give the same value.</summary>
internal static class EngineVersion
{
    /// <summary>Gets the engine version, without the commit that the build adds after '+'.</summary>
    public static string Value { get; } =
        (typeof(EngineVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}

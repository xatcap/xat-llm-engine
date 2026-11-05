namespace XatLlmEngine.Host;

/// <summary>Log messages written while the engine starts.</summary>
internal static partial class StartupLog
{
    /// <summary>Logs which providers can serve requests, which makes a forgotten API key easy to notice.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="providers">The provider names.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Enabled providers: {Providers}")]
    public static partial void EnabledProviders(ILogger logger, string providers);

    /// <summary>Logs whether callers need a key, which makes a mistyped key setting easy to notice.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="keyCount">The number of accepted keys. Zero means that no key is required.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Accepted API keys: {KeyCount}. With none, the API requires no key and stays on loopback.")]
    public static partial void ApiKeys(ILogger logger, int keyCount);

    /// <summary>Logs an unexpected failure while handling a request.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "A request failed unexpectedly.")]
    public static partial void UnhandledRequestFailure(ILogger logger, Exception exception);
}

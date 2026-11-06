namespace XatLlmEngine.Host;

/// <summary>Log messages written while the engine starts.</summary>
internal static partial class StartupLog
{
    /// <summary>Logs which providers can serve requests, which makes a forgotten API key easy to notice.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="providers">The provider names.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Enabled providers: {Providers}")]
    public static partial void EnabledProviders(ILogger logger, string providers);

    /// <summary>Logs that the data folder depends on the install folder. An app update that moves the engine to a new folder then also starts new usage counts.</summary>
    /// <param name="logger">The log.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine:DataFolder is empty, so the data folder depends on the install folder. If an app update moves the engine to a new folder, the token budgets start again from zero. An app that includes the engine must set Engine:DataFolder to a folder that does not change.")]
    public static partial void DefaultDataFolder(ILogger logger);

    /// <summary>Logs an unexpected failure while handling a request.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "A request failed unexpectedly.")]
    public static partial void UnhandledRequestFailure(ILogger logger, Exception exception);
}

namespace XatLlmEngine.Domain;

/// <summary>Describes an expected failure.</summary>
/// <param name="Kind">The business category of the failure.</param>
/// <param name="Message">A message that is safe to show to the API caller. It must never contain secrets.</param>
/// <param name="RetryAfter">How long the caller should wait before a retry, when the provider asked for a wait. Otherwise <see langword="null"/>.</param>
public sealed record EngineError(ErrorKind Kind, string Message, TimeSpan? RetryAfter = null);

namespace XatLlmEngine.Domain;

/// <summary>Describes an expected failure.</summary>
/// <param name="Kind">The business category of the failure.</param>
/// <param name="Message">A message that is safe to show to the API caller. It must never contain secrets.</param>
public sealed record EngineError(ErrorKind Kind, string Message);

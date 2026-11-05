namespace XatLlmEngine.Host.Contracts;

/// <summary>An error in the OpenAI format, which OpenAI client libraries know how to read.</summary>
/// <param name="Error">The details.</param>
internal sealed record ErrorResponse(ErrorDetails Error);

/// <summary>The details of an error.</summary>
/// <param name="Message">A description that is safe to show to the caller.</param>
/// <param name="Type">The error category.</param>
internal sealed record ErrorDetails(string Message, string Type);

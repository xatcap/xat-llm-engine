using XatLlmEngine.Domain;

namespace XatLlmEngine.Host.Contracts;

/// <summary>Maps engine errors to HTTP statuses and the OpenAI error format.</summary>
internal static class ErrorMapping
{
    /// <summary>The seconds a caller should wait before retrying a temporary failure.</summary>
    private const string RetryAfterSeconds = "5";

    /// <summary>Builds the HTTP status for an error.</summary>
    /// <param name="kind">The error category.</param>
    /// <returns>The status code.</returns>
    public static int ToStatusCode(ErrorKind kind) => kind switch
    {
        ErrorKind.InvalidRequest => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status502BadGateway,
    };

    /// <summary>Builds the response body for an error.</summary>
    /// <param name="error">The error.</param>
    /// <returns>The body.</returns>
    public static ErrorResponse ToBody(EngineError error) => new(new ErrorDetails(error.Message, error.Kind switch
    {
        ErrorKind.InvalidRequest => "invalid_request_error",
        ErrorKind.NotFound => "not_found_error",
        ErrorKind.RateLimited => "rate_limit_error",
        ErrorKind.Unavailable => "service_unavailable_error",
        _ => "upstream_error",
    }));

    /// <summary>Builds the complete HTTP result for an error, and asks callers to wait before retrying when the failure is temporary.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="error">The error.</param>
    /// <returns>The result to return from the endpoint.</returns>
    public static IResult ToResult(HttpContext context, EngineError error)
    {
        if (error.Kind is ErrorKind.Unavailable or ErrorKind.RateLimited)
        {
            context.Response.Headers.RetryAfter = RetryAfterSeconds;
        }

        return Results.Json(ToBody(error), HostJson.Default.ErrorResponse, statusCode: ToStatusCode(error.Kind));
    }
}

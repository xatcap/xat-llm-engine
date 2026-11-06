using System.Globalization;
using XatLlmEngine.Domain;

namespace XatLlmEngine.Host.Contracts;

/// <summary>The only place that builds error responses. It maps every error to its HTTP status and the OpenAI error format.</summary>
internal static class ErrorMapping
{
    /// <summary>The seconds a caller should wait before retrying a temporary failure, when the provider asked for no wait.</summary>
    private const string DefaultRetryAfterSeconds = "5";

    /// <summary>The error category of a request the caller must change.</summary>
    private const string InvalidRequestType = "invalid_request_error";

    /// <summary>The error category of a failure inside the engine.</summary>
    private const string InternalType = "internal_error";

    /// <summary>Builds the HTTP status for an error.</summary>
    /// <param name="kind">The error category.</param>
    /// <returns>The status code.</returns>
    public static int ToStatusCode(ErrorKind kind) => kind switch
    {
        ErrorKind.InvalidRequest => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
        ErrorKind.Upstream => StatusCodes.Status502BadGateway,
        ErrorKind.Overloaded => StatusCodes.Status503ServiceUnavailable,
        ErrorKind.BudgetExceeded => StatusCodes.Status429TooManyRequests,
    };

    /// <summary>Builds the response body for an error.</summary>
    /// <param name="error">The error.</param>
    /// <returns>The body.</returns>
    public static ErrorResponse ToBody(EngineError error) => new(new ErrorDetails(error.Message, error.Kind switch
    {
        ErrorKind.InvalidRequest => InvalidRequestType,
        ErrorKind.NotFound => "not_found_error",
        ErrorKind.RateLimited => "rate_limit_error",
        ErrorKind.Unavailable => "service_unavailable_error",
        ErrorKind.Upstream => "upstream_error",
        ErrorKind.Overloaded => "overloaded_error",
        ErrorKind.BudgetExceeded => "budget_exceeded_error",
    }));

    /// <summary>Builds the complete HTTP result for an error, and asks callers to wait before retrying when the failure is temporary. The wait is the provider's own, when the provider sent one. The wait is always at least 1 second, so that callers do not retry immediately.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="error">The error.</param>
    /// <returns>The result to return from the endpoint.</returns>
    public static IResult ToResult(HttpContext context, EngineError error)
    {
        if (error.Kind is ErrorKind.Unavailable or ErrorKind.RateLimited or ErrorKind.Overloaded or ErrorKind.BudgetExceeded)
        {
            context.Response.Headers.RetryAfter = error.RetryAfter is { } delay
                ? Math.Max(1, (long)Math.Ceiling(delay.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
                : DefaultRetryAfterSeconds;
        }

        return Results.Json(ToBody(error), HostJson.Default.ErrorResponse, statusCode: ToStatusCode(error.Kind));
    }

    /// <summary>Builds the result for a request that is not JSON.</summary>
    /// <returns>A 415 result.</returns>
    public static IResult UnsupportedMediaType() =>
        Json("The Content-Type must be application/json.", InvalidRequestType, StatusCodes.Status415UnsupportedMediaType);

    /// <summary>Builds the result for a request body that is larger than the limit.</summary>
    /// <param name="limit">The limit, in bytes.</param>
    /// <returns>A 413 result.</returns>
    public static IResult RequestTooLarge(long limit) =>
        Json($"The request body is larger than {limit} bytes. Send a smaller request, or increase Api:MaxRequestBodyBytes.", InvalidRequestType, StatusCodes.Status413PayloadTooLarge);

    /// <summary>Builds the result for an unexpected failure. It never holds the failure's details.</summary>
    /// <returns>A 500 result.</returns>
    public static IResult Unexpected() =>
        Json("The engine failed unexpectedly. Report the problem with the X-Request-Id header of this response. The engine log has the cause.", InternalType, StatusCodes.Status500InternalServerError);

    /// <summary>Writes the body of a response that ASP.NET ended with only a status, such as an unknown route.</summary>
    /// <param name="context">The current request.</param>
    /// <returns>A task that completes after the engine writes the body.</returns>
    public static Task WriteStatusAsync(HttpContext context)
    {
        var status = context.Response.StatusCode;
        var result = status switch
        {
            StatusCodes.Status404NotFound => Json("This route does not exist.", "not_found_error", status),
            StatusCodes.Status405MethodNotAllowed => Json("This route does not support this HTTP method.", InvalidRequestType, status),
            StatusCodes.Status415UnsupportedMediaType => UnsupportedMediaType(),
            >= StatusCodes.Status500InternalServerError => Json("The engine could not process the request.", InternalType, status),
            _ => Json("The engine cannot process this request.", InvalidRequestType, status),
        };
        return result.ExecuteAsync(context);
    }

    /// <summary>Builds a JSON error result.</summary>
    /// <param name="message">A description that is safe to show to the caller.</param>
    /// <param name="type">The error category.</param>
    /// <param name="status">The HTTP status.</param>
    /// <returns>The result.</returns>
    private static IResult Json(string message, string type, int status) =>
        Results.Json(new ErrorResponse(new ErrorDetails(message, type)), HostJson.Default.ErrorResponse, statusCode: status);
}

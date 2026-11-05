using System.Text.Json;
using XatLlmEngine.Application.Features.SystemPrompts;
using XatLlmEngine.Domain;
using XatLlmEngine.Host.Contracts;

namespace XatLlmEngine.Host.Endpoints;

/// <summary>Implements the routes under <c>/v1/system-prompts</c>.</summary>
internal static class SystemPromptEndpoint
{
    /// <summary>Replaces a provider's prompt.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="context">The current request.</param>
    /// <param name="handler">The use case.</param>
    /// <returns>The HTTP result.</returns>
    public static Task<IResult> ReplaceAsync(string provider, HttpContext context, ReplaceSystemPromptHandler handler) =>
        ChangeAsync(provider, context, handler.Handle);

    /// <summary>Adds text to the end of a provider's prompt.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="context">The current request.</param>
    /// <param name="handler">The use case.</param>
    /// <returns>The HTTP result.</returns>
    public static Task<IResult> AppendAsync(string provider, HttpContext context, AppendSystemPromptHandler handler) =>
        ChangeAsync(provider, context, handler.Handle);

    /// <summary>Reads the request and applies one change.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="context">The current request.</param>
    /// <param name="handle">The use case to run.</param>
    /// <returns>The HTTP result.</returns>
    private static async Task<IResult> ChangeAsync(string provider, HttpContext context, Func<SystemPromptCommand, Result<string>> handle)
    {
        // The engine requires JSON content because browsers cannot send it across sites without a CORS approval, which the engine never gives.
        if (!context.Request.HasJsonContentType())
        {
            return ErrorMapping.UnsupportedMediaType();
        }

        SystemPromptRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(context.Request.Body, HostJson.Default.SystemPromptRequest, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller disconnected, so nobody is left to answer.
            return Results.Empty;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return ErrorMapping.ToResult(context, new EngineError(ErrorKind.InvalidRequest, "The request body must be a JSON object with a text 'prompt' field."));
        }

        if (request?.Prompt is not { } prompt)
        {
            return ErrorMapping.ToResult(context, new EngineError(ErrorKind.InvalidRequest, "The request body must be a JSON object with a text 'prompt' field."));
        }

        var result = handle(new SystemPromptCommand(provider, prompt));
        return result.IsSuccess
            ? Results.Json(new SystemPromptResponse(provider, result.Value!), HostJson.Default.SystemPromptResponse)
            : ErrorMapping.ToResult(context, result.Error);
    }
}

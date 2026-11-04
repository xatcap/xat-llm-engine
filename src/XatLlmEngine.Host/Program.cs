using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using XatLlmEngine.Application;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Host;
using XatLlmEngine.Host.Contracts;
using XatLlmEngine.Host.Endpoints;
using XatLlmEngine.Host.Security;
using XatLlmEngine.Infrastructure;

// The content root is the engine's folder, because the current folder depends on how the engine was started.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

ListenAddressPolicy.EnsureAllowed(builder.Configuration);

builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection(EngineOptions.SectionName));
builder.Services.AddSingleton<ApiKeyEndpointFilter>();
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // The API documentation reads its property names from these settings, so it must match what the API really sends.
    options.SerializerOptions.PropertyNamingPolicy = HostJson.Default.Options.PropertyNamingPolicy;
    options.SerializerOptions.DefaultIgnoreCondition = HostJson.Default.Options.DefaultIgnoreCondition;
    options.SerializerOptions.NumberHandling = HostJson.Default.Options.NumberHandling;
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, HostJson.Default);
});
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<ApiDocumentTransformer>());
builder.Services.AddApplication().AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var failure = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (failure is not null)
    {
        StartupLog.UnhandledRequestFailure(context.RequestServices.GetRequiredService<ILogger<Program>>(), failure);
    }

    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    var body = new ErrorResponse(new ErrorDetails("The engine failed unexpectedly.", "internal_error"));
    await context.Response.WriteAsJsonAsync(body, HostJson.Default.ErrorResponse).ConfigureAwait(false);
}));

var api = app.MapGroup("/v1").AddEndpointFilter<ApiKeyEndpointFilter>();
api.MapPost("/chat/completions", ChatCompletionsEndpoint.HandleAsync)
    .WithDescription("Set 'stream' to true to receive server-sent events (text/event-stream) instead of JSON. Swagger UI shows a stream only after it has finished.")
    .Accepts<ChatCompletionRequest>("application/json")
    .Produces<ChatCompletionResponse>()
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
    .Produces<ErrorResponse>(StatusCodes.Status415UnsupportedMediaType)
    .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
    .Produces<ErrorResponse>(StatusCodes.Status502BadGateway)
    .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
api.MapGet("/models", ModelsEndpoint.Handle)
    .Produces<ModelListResponse>()
    .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized);
app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.WriteAsync });

// The API documentation is for debugging only. A release package runs as Production, so it does not expose these routes.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "xat-llm-engine"));
}

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
var enabledProviders = string.Join(", ", app.Services.GetServices<IChatProvider>().Select(provider => provider.Name));
var apiKeyCount = app.Services.GetRequiredService<IOptions<EngineOptions>>().Value.EffectiveKeys.Count;
StartupLog.EnabledProviders(startupLogger, enabledProviders);
StartupLog.ApiKeys(startupLogger, apiKeyCount);

await app.RunAsync().ConfigureAwait(false);

/// <summary>The engine's entry point. It is a public partial class so that integration tests can host it.</summary>
public partial class Program;

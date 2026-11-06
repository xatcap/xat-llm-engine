using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using XatLlmEngine.Application;
using XatLlmEngine.Application.Features.Providers;
using XatLlmEngine.Host;
using XatLlmEngine.Host.Contracts;
using XatLlmEngine.Host.Diagnostics;
using XatLlmEngine.Host.Endpoints;
using XatLlmEngine.Host.Lifecycle;
using XatLlmEngine.Host.Security;
using XatLlmEngine.Infrastructure;

// The content root is the engine's folder, because the current folder depends on how the engine started.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

ListenAddressPolicy.EnsureAllowed(builder.Configuration);

builder.Services.AddOptions<ApiOptions>().Bind(builder.Configuration.GetSection(ApiOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
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
builder.Services.AddHostedService<ReadyFileWriter>();
builder.Services.AddHostedService<InputCloseWatcher>();

var app = builder.Build();

// This middleware comes first, so that every log line of a request, also of an unexpected failure, holds its ID.
app.UseMiddleware<RequestIdMiddleware>();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var failure = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (failure is not null)
    {
        StartupLog.UnhandledRequestFailure(context.RequestServices.GetRequiredService<ILogger<Program>>(), failure);
    }

    await ErrorMapping.Unexpected().ExecuteAsync(context).ConfigureAwait(false);
}));
app.UseStatusCodePages(pages => ErrorMapping.WriteStatusAsync(pages.HttpContext));

var api = app.MapGroup("/v1");
api.MapPost("/chat/completions", ChatCompletionsEndpoint.HandleAsync)
    .WithDescription("Set 'stream' to true to receive server-sent events (text/event-stream) instead of JSON. Swagger UI shows a stream only after it has finished.")
    .Accepts<JsonObject>("application/json")
    .Produces<ChatCompletionResponse>()
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
    .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge)
    .Produces<ErrorResponse>(StatusCodes.Status415UnsupportedMediaType)
    .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
    .Produces<ErrorResponse>(StatusCodes.Status502BadGateway)
    .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
api.MapGet("/models", ModelsEndpoint.Handle)
    .Produces<ModelListResponse>();
HealthEndpoints.Map(app);

// The API documentation is for debugging only. A release package runs as Production, so it does not expose these routes.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "xat-llm-engine"));
}

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
// Building the registry here also stops the engine at startup when two providers have the same name.
var enabledProviders = string.Join(", ", app.Services.GetRequiredService<ProviderRegistry>().All.Select(provider => provider.Name));
StartupLog.EnabledProviders(startupLogger, enabledProviders);
if (string.IsNullOrWhiteSpace(app.Services.GetRequiredService<IOptions<EngineOptions>>().Value.DataFolder))
{
    StartupLog.DefaultDataFolder(startupLogger);
}

await app.RunAsync().ConfigureAwait(false);

/// <summary>The engine's entry point. It is a public partial class so that integration tests can host it.</summary>
public partial class Program;

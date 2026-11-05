using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Infrastructure.LlamaCpp;
using XatLlmEngine.Infrastructure.Providers;
using XatLlmEngine.Infrastructure.Providers.Anthropic;
using XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

namespace XatLlmEngine.Infrastructure;

/// <summary>Registers the Infrastructure layer.</summary>
public static class DependencyInjection
{
    /// <summary>The HTTP client for llama-server. It has no timeout or retries, because local generation can be slow and a local failure will not fix itself.</summary>
    internal const string LocalHttpClientName = "provider-local";

    /// <summary>Adds the local model and every cloud provider that has an API key.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same collection, so that the caller can chain calls.</returns>
    /// <exception cref="InvalidOperationException">A cloud provider's settings are invalid.</exception>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // The host or a test can choose another clock before this call.
        services.TryAddSingleton(TimeProvider.System);
        AddLocalModel(services, configuration);

        var providers = configuration.GetSection(ProviderOptions.SectionName).Get<Dictionary<string, ProviderOptions>>() ?? [];
        foreach (var (name, options) in providers)
        {
            Validate(name, options);
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                AddCloudProvider(services, name, options);
            }
        }

        return services;
    }

    /// <summary>Adds the supervised llama-server, unless the settings turn off the local model.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configuration">The application configuration.</param>
    private static void AddLocalModel(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(LlamaCppOptions.SectionName);
        services.AddOptions<LlamaCppOptions>().Bind(section).ValidateDataAnnotations().ValidateOnStart();

        if (!(section.Get<LlamaCppOptions>() ?? new LlamaCppOptions()).Enabled)
        {
            return;
        }

        services.AddHttpClient(LocalHttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<LlamaServerSupervisor>();
        services.AddHostedService(provider => provider.GetRequiredService<LlamaServerSupervisor>());
        services.AddSingleton<IChatProvider>(provider => new LocalChatProvider(
            provider.GetRequiredService<LlamaServerSupervisor>(),
            new OpenAiClient(
                LocalChatProvider.ProviderName,
                provider.GetRequiredService<IHttpClientFactory>(),
                LocalHttpClientName,
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<OpenAiClient>>()),
            provider.GetRequiredService<IOptions<LlamaCppOptions>>()));
        services.AddHealthChecks().AddCheck<LlamaServerHealthCheck>(LocalModelHealth.EntryName);
    }

    /// <summary>Adds one cloud provider and its HTTP client.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="name">The provider's name.</param>
    /// <param name="options">The provider's validated settings.</param>
    private static void AddCloudProvider(IServiceCollection services, string name, ProviderOptions options)
    {
        var clientName = $"provider-{name}";
        services
            .AddHttpClient(clientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddResilienceHandler("upstream", (pipeline, context) =>
            {
                var timeProvider = context.ServiceProvider.GetRequiredService<TimeProvider>();

                // The limits cover waiting for a response to start. A stream that has started can run as long as it needs.
                pipeline.AddTimeout(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                if (options.MaxRetries > 0)
                {
                    pipeline.AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = options.MaxRetries,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        MaxDelay = HttpFailure.MaximumRetryDelay,
                        DelayGenerator = arguments => ValueTask.FromResult(HttpFailure.RetryDelay(arguments.Outcome.Result, timeProvider.GetUtcNow())),
                    });
                }
            });

        services.AddSingleton<IChatProvider>(provider =>
        {
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            var timeProvider = provider.GetRequiredService<TimeProvider>();
            return options.Type == ProviderType.Anthropic
                ? new AnthropicChatProvider(name, options, factory, clientName, timeProvider, provider.GetRequiredService<ILogger<AnthropicChatProvider>>())
                : new OpenAiCompatibleChatProvider(name, options, new OpenAiClient(name, factory, clientName, timeProvider, provider.GetRequiredService<ILogger<OpenAiClient>>()));
        });
    }

    /// <summary>Checks a cloud provider's settings, so that a mistake stops the engine at startup rather than failing on a request.</summary>
    /// <param name="name">The provider's name.</param>
    /// <param name="options">The provider's settings.</param>
    /// <exception cref="InvalidOperationException">The settings are invalid.</exception>
    internal static void Validate(string name, ProviderOptions options)
    {
        if (string.Equals(name, LocalChatProvider.ProviderName, StringComparison.OrdinalIgnoreCase) || name.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"A provider cannot use the name '{name}'. The engine reserves the name '{LocalChatProvider.ProviderName}', and names cannot contain '/'.");
        }

        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true))
        {
            throw new InvalidOperationException($"The settings of provider '{name}' are invalid: {string.Join(' ', results.Select(result => result.ErrorMessage))}");
        }

        if (options.BaseUrl is not { IsAbsoluteUri: true })
        {
            throw new InvalidOperationException($"The BaseUrl of provider '{name}' must be an absolute address.");
        }

        // Every request includes the provider's API key, so the engine allows plain HTTP only on this machine.
        if (options.BaseUrl.Scheme != Uri.UriSchemeHttps && !(options.BaseUrl.Scheme == Uri.UriSchemeHttp && options.BaseUrl.IsLoopback))
        {
            throw new InvalidOperationException($"The BaseUrl of provider '{name}' must use https, or http on this machine only.");
        }
    }
}

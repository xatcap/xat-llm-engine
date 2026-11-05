using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
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

    /// <summary>The longest wait before one retry, including when a provider's Retry-After header asks for longer.</summary>
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Adds the local model and every cloud provider that has an API key.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same collection, so calls can be chained.</returns>
    /// <exception cref="InvalidOperationException">A cloud provider's settings are invalid.</exception>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddTimeProvider();
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

    /// <summary>Adds the clock unless the host or a test has already chosen one.</summary>
    /// <param name="services">The service collection to add to.</param>
    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (services.All(service => service.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }

    /// <summary>Adds the supervised llama-server unless the local model is switched off in settings.</summary>
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
            new OpenAiClient(LocalChatProvider.ProviderName, provider.GetRequiredService<IHttpClientFactory>(), LocalHttpClientName, useMaxCompletionTokens: false),
            provider.GetRequiredService<IOptions<LlamaCppOptions>>()));
        services.AddHealthChecks().AddCheck<LlamaServerHealthCheck>(LocalChatProvider.ProviderName);
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
            .AddResilienceHandler("upstream", pipeline =>
            {
                // The limits cover waiting for a response to start. A stream that has started can run as long as it needs.
                pipeline.AddTimeout(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                if (options.MaxRetries > 0)
                {
                    pipeline.AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = options.MaxRetries,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        MaxDelay = MaximumRetryDelay,
                        DelayGenerator = arguments => ValueTask.FromResult(RetryDelay(arguments.Outcome.Result, TimeProvider.System.GetUtcNow())),
                    });
                }
            });

        services.AddSingleton<IChatProvider>(provider =>
        {
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            return options.Type == ProviderType.Anthropic
                ? new AnthropicChatProvider(name, options, factory, clientName)
                : new OpenAiCompatibleChatProvider(name, options, new OpenAiClient(name, factory, clientName, options.UseMaxCompletionTokens));
        });
    }

    /// <summary>Follows a provider's Retry-After header, up to the limit. MaxDelay alone does not limit it, so a provider could otherwise hold a caller's request for many minutes.</summary>
    /// <param name="response">The response to retry, if there was one.</param>
    /// <param name="now">The current time, for a header that holds a date.</param>
    /// <returns>The delay to use, or <see langword="null"/> to use the normal backoff.</returns>
    internal static TimeSpan? RetryDelay(HttpResponseMessage? response, DateTimeOffset now)
    {
        var header = response?.Headers.RetryAfter;
        var requested = header?.Delta ?? (header?.Date - now);
        if (requested is not { } delay)
        {
            return null;
        }

        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay < MaximumRetryDelay ? delay : MaximumRetryDelay;
    }

    /// <summary>Checks a cloud provider's settings, so that a mistake stops the engine at startup rather than failing on a request.</summary>
    /// <param name="name">The provider's name.</param>
    /// <param name="options">The provider's settings.</param>
    /// <exception cref="InvalidOperationException">The settings are invalid.</exception>
    internal static void Validate(string name, ProviderOptions options)
    {
        if (string.Equals(name, LocalChatProvider.ProviderName, StringComparison.OrdinalIgnoreCase) || name.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{name}' cannot be used as a provider name. The name '{LocalChatProvider.ProviderName}' is reserved, and names cannot contain '/'.");
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

        // A provider's API key travels with every request, so plain HTTP is allowed only when it stays on this machine.
        if (options.BaseUrl.Scheme != Uri.UriSchemeHttps && !(options.BaseUrl.Scheme == Uri.UriSchemeHttp && options.BaseUrl.IsLoopback))
        {
            throw new InvalidOperationException($"The BaseUrl of provider '{name}' must use https, or http on this machine only.");
        }
    }
}

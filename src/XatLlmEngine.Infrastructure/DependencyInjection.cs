using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Infrastructure.LlamaCpp;
using XatLlmEngine.Infrastructure.Providers;
using XatLlmEngine.Infrastructure.Providers.OpenAiCompatible;

namespace XatLlmEngine.Infrastructure;

/// <summary>Registers the Infrastructure layer.</summary>
public static class DependencyInjection
{
    /// <summary>The HTTP client for llama-server. It has no time limits, because local generation can be slow. The supervisor finds a server that hangs.</summary>
    internal const string LocalHttpClientName = "provider-local";

    /// <summary>Adds the local model and every cloud provider that has an API key.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same collection, so that the caller can chain calls.</returns>
    /// <exception cref="InvalidOperationException">The local model settings or a cloud provider's settings are invalid.</exception>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // The host or a test can choose another clock before this call.
        services.TryAddSingleton(TimeProvider.System);
        AddEngine(services, configuration);
        var disabled = new List<string>();
        if (!AddLocalModel(services, configuration))
        {
            disabled.Add(LocalChatProvider.ProviderName);
        }

        foreach (var (name, options) in ProviderSettings.Read(configuration))
        {
            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                disabled.Add(name);
            }
            else
            {
                AddCloudProvider(services, name, options);
            }
        }

        services.AddSingleton<IDisabledProviders>(new DisabledProviders(disabled));
        return services;
    }

    /// <summary>Adds the engine settings, the data folder and the lock that allows only one engine for each install folder.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <exception cref="InvalidOperationException">The engine settings are invalid.</exception>
    private static void AddEngine(IServiceCollection services, IConfiguration configuration)
    {
        var problems = new List<string>();
        var options = SettingsRules.Bind<EngineOptions>(configuration.GetSection(EngineOptions.SectionName), problems);
        var readyFile = ResolveReadyFile(options.ReadyFile, problems);
        SettingsRules.ThrowIfAny($"'{EngineOptions.SectionName}'", problems);

        var dataFolder = string.IsNullOrWhiteSpace(options.DataFolder)
            ? EngineDataFolder.ForThisInstall()
            : new EngineDataFolder(LlamaServerPaths.FromEngineFolder(options.DataFolder));
        services.AddSingleton(dataFolder);

        // One validated object is the only source of these settings. The ready file path is absolute, so the Host does not resolve it again.
        services.AddSingleton(Options.Create(new EngineOptions
        {
            DataFolder = options.DataFolder,
            ReadyFile = readyFile,
            StopWhenInputCloses = options.StopWhenInputCloses,
        }));
        services.AddSingleton<EngineInstanceLock>();
        services.AddHostedService(provider => provider.GetRequiredService<EngineInstanceLock>());
        services.AddSingleton<JsonUsageLedger>();
        services.AddSingleton<IUsageLedger>(provider => provider.GetRequiredService<JsonUsageLedger>());
        services.AddHostedService(provider => provider.GetRequiredService<JsonUsageLedger>());
    }

    /// <summary>Resolves the path of the ready file. A path that cannot work stops the engine at startup, before the model loads.</summary>
    /// <param name="setting">The configured path.</param>
    /// <param name="problems">Receives each problem.</param>
    /// <returns>The absolute path, or an empty value when the setting is off.</returns>
    private static string ResolveReadyFile(string setting, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            return string.Empty;
        }

        try
        {
            var path = LlamaServerPaths.FromEngineFolder(setting);
            if (!Directory.Exists(Path.GetDirectoryName(path)))
            {
                problems.Add($"The folder of the ready file '{path}' does not exist. Create the folder before the engine starts.");
            }

            return path;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problems.Add($"The path of the ready file '{setting}' is not valid: {exception.Message}");
            return string.Empty;
        }
    }

    /// <summary>Adds the supervised llama-server, unless the settings turn off the local model.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns><see langword="true"/> when the local model is on.</returns>
    /// <exception cref="InvalidOperationException">The local model settings are invalid. The engine checks them also when the local model is off.</exception>
    private static bool AddLocalModel(IServiceCollection services, IConfiguration configuration)
    {
        var problems = new List<string>();
        var options = SettingsRules.Bind<LlamaCppOptions>(configuration.GetSection(LlamaCppOptions.SectionName), problems);
        SettingsRules.Validate(options, problems);
        SettingsRules.ThrowIfAny($"'{LlamaCppOptions.SectionName}'", problems);

        // One validated object is the only source of these settings.
        services.AddSingleton(Options.Create(options));
        if (!options.Enabled)
        {
            return false;
        }

        AddHttpClient(services, LocalHttpClientName);
        services.AddSingleton(provider => new LocalRequestGate(options.EffectiveParallel, options.MaxQueuedRequests, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<LlamaServerSupervisor>();
        services.AddHostedService(provider => provider.GetRequiredService<LlamaServerSupervisor>());
        services.AddSingleton<IChatProvider>(provider => new LocalChatProvider(
            provider.GetRequiredService<LlamaServerSupervisor>(),
            provider.GetRequiredService<LocalRequestGate>(),
            new OpenAiClient(
                new ProviderTransport(
                    new TransportSettings(LocalChatProvider.ProviderName, ProviderKind.Local, LocalHttpClientName, RequestTimeout: null, ReadIdleTimeout: null),
                    provider.GetRequiredService<IHttpClientFactory>(),
                    provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ILogger<OpenAiClient>>()),
                options.RequestFields.Contains("stream_options", StringComparer.Ordinal)),
            provider.GetRequiredService<IOptions<LlamaCppOptions>>()));
        return true;
    }

    /// <summary>Adds one cloud provider and its HTTP client.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="name">The provider's name.</param>
    /// <param name="options">The provider's validated settings.</param>
    private static void AddCloudProvider(IServiceCollection services, string name, ProviderOptions options)
    {
        var clientName = $"provider-{name}";
        var factory = ChatProviderFactories.For(name, options.Type!.Value);

        // The engine never retries a request. A retry of a generation can cost money twice, so the caller decides. See ADR 0006.
        // The transport applies the time limits, because they differ for the headers and for the body.
        AddHttpClient(services, clientName);
        services.AddSingleton(provider => factory.Create(name, options, clientName, provider));
    }

    /// <summary>The providers that the configuration has, but that are turned off.</summary>
    /// <param name="names">The names.</param>
    private sealed class DisabledProviders(IReadOnlyList<string> names) : IDisabledProviders
    {
        /// <inheritdoc/>
        public IReadOnlyList<string> Names => names;
    }

    /// <summary>Adds a named HTTP client with no time limit of its own, and with redirects turned off.</summary>
    /// <remarks>.NET removes the Authorization header on a redirect to another host, but it keeps other headers such as Anthropic's x-api-key.</remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="name">The client name.</param>
    private static void AddHttpClient(IServiceCollection services, string name) =>
        services
            .AddHttpClient(name, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
}

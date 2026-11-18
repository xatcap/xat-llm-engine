using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
    /// <summary>The HTTP client for llama-server. It has no time limits, because local generation can be slow. The supervisor finds a server that hangs. Both models use it, because it holds no state for one server.</summary>
    internal const string LocalHttpClientName = "provider-local";

    /// <summary>Adds the main local model and the small query-expansion model.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same collection, so that the caller can chain calls.</returns>
    /// <exception cref="InvalidOperationException">The settings of a model are invalid.</exception>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // The host or a test can choose another clock before this call.
        services.TryAddSingleton(TimeProvider.System);
        AddEngine(services, configuration);

        // Both sections are checked before anything is registered, also when a model is off.
        var main = ReadModel<LlamaCppOptions>(configuration, LlamaModelRole.Main);
        var queryExpansion = ReadModel<QueryExpansionOptions>(configuration, LlamaModelRole.QueryExpansion);
        services.AddSingleton(Options.Create(main));
        services.AddSingleton(Options.Create(queryExpansion));
        AddHttpClient(services, LocalHttpClientName);

        var disabled = new List<string>();
        foreach (var (role, options) in new (LlamaModelRole, LlamaCppOptions)[] { (LlamaModelRole.Main, main), (LlamaModelRole.QueryExpansion, queryExpansion) })
        {
            if (options.Enabled)
            {
                AddLlamaModel(services, role, options);
            }
            else
            {
                disabled.Add(role.ProviderName);
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

    /// <summary>Reads and checks the settings of one local model.</summary>
    /// <typeparam name="TOptions">The settings type of the model.</typeparam>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="role">The identity of the model.</param>
    /// <returns>The valid settings.</returns>
    /// <exception cref="InvalidOperationException">The settings are invalid. The engine checks them also when the model is off.</exception>
    private static TOptions ReadModel<TOptions>(IConfiguration configuration, LlamaModelRole role)
        where TOptions : LlamaCppOptions, new()
    {
        var problems = new List<string>();
        var options = SettingsRules.Bind<TOptions>(configuration.GetSection(role.SectionName), problems);
        SettingsRules.Validate(options, problems);
        SettingsRules.ThrowIfAny($"'{role.SectionName}'", problems);
        return options;
    }

    /// <summary>Adds the request gate, the supervised llama-server and the provider of one local model. Each model has its own set, found by the provider name.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="role">The identity of the model.</param>
    /// <param name="options">The valid settings of the model.</param>
    private static void AddLlamaModel(IServiceCollection services, LlamaModelRole role, LlamaCppOptions options)
    {
        var key = role.ProviderName;
        services.AddKeyedSingleton(key, (provider, _) => new LocalRequestGate(options.EffectiveParallel, options.MaxQueuedRequests, provider.GetRequiredService<TimeProvider>(), role));
        services.AddKeyedSingleton(key, (provider, _) => new LlamaServerSupervisor(
            Options.Create(options),
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredKeyedService<LocalRequestGate>(key),
            provider.GetRequiredService<EngineDataFolder>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<LlamaServerSupervisor>>(),
            provider.GetService<IMeterFactory>(),
            role));

        // AddHostedService with a factory skips a second service of the same type, so each model registers its own service by hand.
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredKeyedService<LlamaServerSupervisor>(key));
        services.AddSingleton<IChatProvider>(provider => new LocalChatProvider(
            provider.GetRequiredKeyedService<LlamaServerSupervisor>(key),
            provider.GetRequiredKeyedService<LocalRequestGate>(key),
            new OpenAiClient(
                new ProviderTransport(
                    new TransportSettings(role.ProviderName, LocalHttpClientName),
                    provider.GetRequiredService<IHttpClientFactory>(),
                    provider.GetRequiredService<ILogger<OpenAiClient>>()),
                options.RequestFields.Contains(OpenAiClient.StreamOptionsField, StringComparer.Ordinal)),
            Options.Create(options),
            role));
    }

    /// <summary>The providers that the configuration has, but that are turned off.</summary>
    /// <param name="names">The names.</param>
    private sealed class DisabledProviders(IReadOnlyList<string> names) : IDisabledProviders
    {
        /// <inheritdoc/>
        public IReadOnlyList<string> Names => names;
    }

    /// <summary>Adds a named HTTP client with no time limit of its own, and with redirects turned off.</summary>
    /// <remarks>.NET removes the Authorization header on a redirect to another host, but it keeps other headers.</remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="name">The client name.</param>
    private static void AddHttpClient(IServiceCollection services, string name) =>
        services
            .AddHttpClient(name, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
}

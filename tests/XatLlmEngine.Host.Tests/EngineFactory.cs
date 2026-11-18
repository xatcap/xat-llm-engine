using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using XatLlmEngine.Application.Abstractions;

namespace XatLlmEngine.Host.Tests;

/// <summary>Hosts the real API in memory with a fake provider. The local model files are absent, so <c>local</c> reports missing. The real query-expansion model is off, because the tests use a fake one when they need it.</summary>
/// <param name="settings">Settings that replace the values in appsettings.json, as configuration keys such as <c>Api:MaxRequestBodyBytes</c>.</param>
/// <param name="timeProvider">The clock of the engine, or <see langword="null"/> for the system clock.</param>
/// <param name="withQueryExpansion">Tells whether the engine has a fake provider for the query-expansion model.</param>
internal sealed class EngineFactory(IReadOnlyDictionary<string, string?>? settings = null, TimeProvider? timeProvider = null, bool withQueryExpansion = false) : WebApplicationFactory<Program>
{
    /// <summary>Gets the fake provider, available as <c>fake/...</c>.</summary>
    public FakeProvider Provider { get; } = new();

    /// <summary>Gets the fake provider for the query-expansion model, available as <c>query-expansion/...</c>. The engine uses it only when <c>withQueryExpansion</c> is <see langword="true"/>.</summary>
    public FakeProvider QueryExpansion { get; } = new(ProviderNames.QueryExpansion);

    /// <summary>Gets the data folder of this engine. Each engine has its own, because one engine holds the instance lock of its folder.</summary>
    public string DataFolder { get; } = Directory.CreateTempSubdirectory("xat-engine-test-").FullName;

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Engine:DataFolder", DataFolder);
        builder.UseSetting("QueryExpansionModel:Enabled", "false");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IChatProvider>(Provider);
            if (withQueryExpansion)
            {
                services.AddSingleton<IChatProvider>(QueryExpansion);
            }

            if (timeProvider is not null)
            {
                services.AddSingleton(timeProvider);
            }
        });
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(DataFolder, recursive: true);
    }
}

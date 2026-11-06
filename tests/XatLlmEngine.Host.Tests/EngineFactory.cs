using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using XatLlmEngine.Application.Abstractions;

namespace XatLlmEngine.Host.Tests;

/// <summary>Hosts the real API in memory with a fake provider. The local model files are absent, so <c>local</c> reports missing.</summary>
/// <param name="settings">Settings that replace the values in appsettings.json, as configuration keys such as <c>Api:MaxRequestBodyBytes</c>.</param>
/// <param name="dataFolder">The data folder to use, for a test of a restart. The factory then does not delete it. Without it, the factory makes a new folder.</param>
/// <param name="timeProvider">The clock of the engine, or <see langword="null"/> for the system clock.</param>
internal sealed class EngineFactory(IReadOnlyDictionary<string, string?>? settings = null, string? dataFolder = null, TimeProvider? timeProvider = null) : WebApplicationFactory<Program>
{
    /// <summary>Gets the fake provider, available as <c>fake/...</c>.</summary>
    public FakeProvider Provider { get; } = new();

    /// <summary>Gets the data folder of this engine. Each engine has its own, because one engine holds the instance lock of its folder.</summary>
    public string DataFolder { get; } = dataFolder ?? Directory.CreateTempSubdirectory("xat-engine-test-").FullName;

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Engine:DataFolder", DataFolder);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IChatProvider>(Provider);
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
        if (dataFolder is null)
        {
            Directory.Delete(DataFolder, recursive: true);
        }
    }
}

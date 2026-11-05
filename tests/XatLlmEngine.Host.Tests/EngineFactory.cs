using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Host.Security;

namespace XatLlmEngine.Host.Tests;

/// <summary>Hosts the real API in memory with a fake provider. The local model files are absent, so <c>local</c> reports missing.</summary>
/// <param name="apiKeys">The keys the API should require.</param>
internal sealed class EngineFactory(params string[] apiKeys) : WebApplicationFactory<Program>
{
    /// <summary>Gets the fake provider, available as <c>fake/...</c>.</summary>
    public FakeProvider Provider { get; } = new();

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IChatProvider>(Provider);
            services.AddSingleton<IOptions<EngineOptions>>(Options.Create(new EngineOptions { ApiKeys = apiKeys }));
        });
    }
}

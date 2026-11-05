using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using XatLlmEngine.Application.Abstractions;

namespace XatLlmEngine.Host.Tests;

/// <summary>Hosts the real API in memory with a fake provider. The local model files are absent, so <c>local</c> reports missing.</summary>
internal sealed class EngineFactory : WebApplicationFactory<Program>
{
    /// <summary>Gets the fake provider, available as <c>fake/...</c>.</summary>
    public FakeProvider Provider { get; } = new();

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IChatProvider>(Provider);
        });
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Infrastructure.LlamaCpp;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the real composition root, <see cref="DependencyInjection.AddInfrastructure"/>, with settings in memory.</summary>
public class CompositionRootTests
{
    /// <summary>Both models are on by default. Each one has its own provider name.</summary>
    [Fact]
    public void AddInfrastructure_BothModelsEnabled_RegistersBothProviders()
    {
        using var services = Build(Settings());

        var names = services.GetServices<IChatProvider>().Select(provider => provider.Name).ToHashSet();

        Assert.Equal(new HashSet<string> { ProviderNames.Local, ProviderNames.QueryExpansion }, names);
        Assert.Empty(services.GetRequiredService<IDisabledProviders>().Names);
    }

    /// <summary>Each model that is on has its own supervisor, and the host starts every one of them.</summary>
    [Fact]
    public void AddInfrastructure_BothModelsEnabled_StartsBothSupervisors()
    {
        using var services = Build(Settings());

        var hosted = services.GetServices<IHostedService>().OfType<LlamaServerSupervisor>().ToArray();

        Assert.Equal(2, hosted.Length);
        Assert.Contains(services.GetRequiredKeyedService<LlamaServerSupervisor>(ProviderNames.Local), hosted);
        Assert.Contains(services.GetRequiredKeyedService<LlamaServerSupervisor>(ProviderNames.QueryExpansion), hosted);
    }

    /// <summary>A model that the settings turn off is not a provider, and it is in the list of turned-off providers.</summary>
    /// <param name="section">The configuration section of the model.</param>
    /// <param name="name">The provider name of the model.</param>
    [Theory]
    [InlineData("LocalModel", ProviderNames.Local)]
    [InlineData("QueryExpansionModel", ProviderNames.QueryExpansion)]
    public void AddInfrastructure_ModelDisabled_ListsItAsDisabled(string section, string name)
    {
        using var services = Build(Settings(($"{section}:Enabled", "false")));

        Assert.DoesNotContain(services.GetServices<IChatProvider>(), provider => provider.Name == name);
        Assert.Contains(name, services.GetRequiredService<IDisabledProviders>().Names);
    }

    /// <summary>The query-expansion model has its own defaults, so that a missing section never loads the main model a second time.</summary>
    [Fact]
    public void AddInfrastructure_NoQueryExpansionSection_UsesSmallModelDefaults()
    {
        using var services = Build(new Dictionary<string, string?> { ["LocalModel:RequestFields:0"] = "messages", ["QueryExpansionModel:RequestFields:0"] = "messages", ["QueryExpansionModel:RequestFields:1"] = "max_tokens" });

        var provider = services.GetServices<IChatProvider>().Single(entry => entry.Name == ProviderNames.QueryExpansion);

        Assert.Equal(["qwen3-0.6b"], provider.Models);
    }

    /// <summary>An unknown setting of a model stops the engine at startup, and the message names the section.</summary>
    /// <param name="section">The configuration section of the model.</param>
    [Theory]
    [InlineData("LocalModel")]
    [InlineData("QueryExpansionModel")]
    public void AddInfrastructure_UnknownSetting_Throws(string section)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Build(Settings(($"{section}:ContextSise", "4096"))).Dispose());

        Assert.Contains("ContextSise", failure.Message, StringComparison.Ordinal);
        Assert.Contains(section, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The settings are checked, also when the model is off.</summary>
    /// <param name="section">The configuration section of the model.</param>
    [Theory]
    [InlineData("LocalModel")]
    [InlineData("QueryExpansionModel")]
    public void AddInfrastructure_InvalidSettings_Throws(string section)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Build(Settings(($"{section}:Enabled", "false"), ($"{section}:Parallel", "0"))).Dispose());

        Assert.Contains("Parallel", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Builds valid settings for both models.</summary>
    /// <param name="settings">More settings, as full configuration keys.</param>
    /// <returns>The settings as configuration keys.</returns>
    private static Dictionary<string, string?> Settings(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["LocalModel:RequestFields:0"] = "messages",
            ["QueryExpansionModel:RequestFields:0"] = "messages",
            ["QueryExpansionModel:RequestFields:1"] = "max_tokens",
        };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        return values;
    }

    /// <summary>Runs the composition root.</summary>
    /// <param name="settings">The configuration keys.</param>
    /// <returns>The built services.</returns>
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}

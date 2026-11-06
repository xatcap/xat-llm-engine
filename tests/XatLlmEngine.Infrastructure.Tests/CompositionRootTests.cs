using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.Providers;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the real composition root, <see cref="DependencyInjection.AddInfrastructure"/>, with settings in memory.</summary>
public class CompositionRootTests
{
    /// <summary>A provider with an API key is on. A provider with a blank key is off.</summary>
    /// <param name="key">The configured key.</param>
    /// <param name="enabled">Tells whether the provider must be on.</param>
    [Theory]
    [InlineData("secret", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void AddInfrastructure_ApiKey_EnablesProvider(string key, bool enabled)
    {
        using var services = Build(Provider("OpenAiCompatible", ("ApiKey", key)));

        Assert.Equal(enabled, services.GetServices<IChatProvider>().Any(provider => provider.Name == "test"));
    }

    /// <summary>Each protocol gets its own adapter.</summary>
    /// <param name="type">The configured Type.</param>
    /// <param name="adapter">The name of the adapter class.</param>
    [Theory]
    [InlineData("OpenAiCompatible", "OpenAiCompatibleChatProvider")]
    [InlineData("openaicompatible", "OpenAiCompatibleChatProvider")]
    [InlineData("Anthropic", "AnthropicChatProvider")]
    public void AddInfrastructure_Type_SelectsAdapter(string type, string adapter)
    {
        using var services = Build(Provider(type, ("ApiKey", "secret"), ("MaxTokensLimit", "100")));

        Assert.Equal(adapter, services.GetServices<IChatProvider>().Single(provider => provider.Name == "test").GetType().Name);
    }

    /// <summary>An Anthropic provider needs a token limit, because the Anthropic API requires one on every request.</summary>
    [Fact]
    public void AddInfrastructure_AnthropicWithoutLimit_Throws()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Build(Provider("Anthropic", ("ApiKey", "secret"))).Dispose());

        Assert.Contains("MaxTokensLimit", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A token limit needs a token field in the template, or the engine could not apply it.</summary>
    [Fact]
    public void AddInfrastructure_LimitWithoutTokenField_Throws()
    {
        var settings = Provider("OpenAiCompatible", ("ApiKey", "secret"), ("MaxTokensLimit", "100"));
        settings.Remove("Providers:test:RequestFields:1");

        var failure = Assert.Throws<InvalidOperationException>(() => Build(settings).Dispose());

        Assert.Contains("max_tokens", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A budget and its period are settings of each cloud provider.</summary>
    [Fact]
    public void AddInfrastructure_Budget_ReachesProvider()
    {
        using var services = Build(Provider("OpenAiCompatible", ("ApiKey", "secret"), ("TokenBudget", "5000"), ("BudgetPeriodHours", "12")));

        var budget = services.GetServices<IChatProvider>().Single(provider => provider.Name == "test").Budget;

        Assert.Equal(new TokenBudget(5000, TimeSpan.FromHours(12)), budget);
    }

    /// <summary>A missing Type, a number, or an unknown name stops the engine at startup. The provider does not become an OpenAI provider.</summary>
    /// <param name="type">The configured Type, or <see langword="null"/> for no Type.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("5")]
    [InlineData("Antropic")]
    public void AddInfrastructure_InvalidType_Throws(string? type)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Build(Provider(type, ("ApiKey", "secret"))).Dispose());

        Assert.Contains("Type", failure.Message, StringComparison.Ordinal);
        Assert.Contains("provider 'test'", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Every protocol has an adapter factory.</summary>
    [Fact]
    public void Factories_EveryType_HasFactory()
    {
        Assert.All(Enum.GetValues<ProviderType>(), type => Assert.True(ChatProviderFactories.ByType.ContainsKey(type)));
    }

    /// <summary>A key with a line break or a space stops the engine at startup, and the message does not show the key.</summary>
    /// <param name="key">The configured key.</param>
    [Theory]
    [InlineData("secret-key\n")]
    [InlineData("secret-key\r\n")]
    [InlineData(" secret-key")]
    [InlineData("secret key")]
    public void AddInfrastructure_KeyWithSpaces_Throws(string key)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Build(Provider("OpenAiCompatible", ("ApiKey", key))).Dispose());

        Assert.Contains("ApiKey", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A setting that does not exist, such as the removed MaxRetries, stops the engine at startup.</summary>
    /// <param name="key">The unknown setting.</param>
    [Theory]
    [InlineData("MaxRetries")]
    [InlineData("ApiKy")]
    public void AddInfrastructure_UnknownProviderSetting_Throws(string key)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Build(Provider("OpenAiCompatible", ("ApiKey", "secret"), (key, "2"))).Dispose());

        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>An unknown local model setting also stops the engine at startup.</summary>
    [Fact]
    public void AddInfrastructure_UnknownLocalSetting_Throws()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Build(Local(("ContextSise", "4096"))).Dispose());

        Assert.Contains("ContextSise", failure.Message, StringComparison.Ordinal);
        Assert.Contains("LocalModel", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The local model settings are checked, also when the local model is off.</summary>
    [Fact]
    public void AddInfrastructure_InvalidLocalSettings_Throws()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Build(Local(("Enabled", "false"), ("Parallel", "0"))).Dispose());

        Assert.Contains("Parallel", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The engine sends a failed request once. It does not retry, because a retry of a generation can cost money twice.</summary>
    /// <param name="status">The provider's answer.</param>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Provider_FailedResponse_SendsOnce(HttpStatusCode status)
    {
        var http = new StubHttpHandler(status, """{"error":{"message":"busy"}}""");
        using var services = Build(Provider("OpenAiCompatible", ("ApiKey", "secret")), http);
        var provider = services.GetServices<IChatProvider>().Single(entry => entry.Name == "test");

        var result = await provider.CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, http.RequestCount);
    }

    /// <summary>A connection failure is also not retried.</summary>
    [Fact]
    public async Task Provider_ConnectionFailure_SendsOnce()
    {
        var http = new StubHttpHandler(HttpStatusCode.OK, "") { Throw = new HttpRequestException("refused") };
        using var services = Build(Provider("OpenAiCompatible", ("ApiKey", "secret")), http);
        var provider = services.GetServices<IChatProvider>().Single(entry => entry.Name == "test");

        var result = await provider.CompleteAsync(TestRequests.Minimal, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorKind.Unavailable, result.Error?.Kind);
        Assert.Equal(1, http.RequestCount);
    }

    /// <summary>Builds the settings of one valid provider named <c>test</c>, with the local model off.</summary>
    /// <param name="type">The Type, or <see langword="null"/> for no Type.</param>
    /// <param name="settings">More settings of the provider.</param>
    /// <returns>The settings as configuration keys.</returns>
    private static Dictionary<string, string?> Provider(string? type, params (string Key, string Value)[] settings)
    {
        var values = Local(("Enabled", "false"));
        values["Providers:test:BaseUrl"] = "https://api.example.com/v1";
        values["Providers:test:RequestFields:0"] = "messages";
        values["Providers:test:RequestFields:1"] = "max_tokens";
        if (type is not null)
        {
            values["Providers:test:Type"] = type;
        }

        foreach (var (key, value) in settings)
        {
            values[$"Providers:test:{key}"] = value;
        }

        return values;
    }

    /// <summary>Builds valid local model settings.</summary>
    /// <param name="settings">More local model settings.</param>
    /// <returns>The settings as configuration keys.</returns>
    private static Dictionary<string, string?> Local(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["LocalModel:RequestFields:0"] = "messages" };
        foreach (var (key, value) in settings)
        {
            values[$"LocalModel:{key}"] = value;
        }

        return values;
    }

    /// <summary>Runs the composition root.</summary>
    /// <param name="settings">The configuration keys.</param>
    /// <param name="network">A stub network for the client of the provider <c>test</c>, or <see langword="null"/> for the real network.</param>
    /// <returns>The built services.</returns>
    private static ServiceProvider Build(Dictionary<string, string?> settings, StubHttpHandler? network = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddInfrastructure(configuration);
        if (network is not null)
        {
            // This registration comes after the engine's own, so it replaces the real network handler.
            services.AddHttpClient("provider-test").ConfigurePrimaryHttpMessageHandler(() => network);
        }

        return services.BuildServiceProvider();
    }
}

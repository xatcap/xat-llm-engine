using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XatLlmEngine.Application.Abstractions;
using XatLlmEngine.Infrastructure.LlamaCpp;
using XatLlmEngine.Infrastructure.Providers;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the rules for the <c>MaxSystemPromptLength</c> settings of the local model and the cloud providers.</summary>
public class SystemPromptLimitSettingsTests
{
    /// <summary>The local model accepts the lowest and highest limit, and the default limit.</summary>
    /// <param name="limit">The configured limit.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(SystemPromptRules.DefaultLength)]
    [InlineData(SystemPromptRules.LengthCeiling)]
    public void LocalModel_LimitInRange_IsValid(int limit)
    {
        Assert.Empty(Problems(new LlamaCppOptions { RequestFields = ["messages"], MaxSystemPromptLength = limit }));
    }

    /// <summary>The local model refuses a limit below 1 or above the ceiling.</summary>
    /// <param name="limit">The configured limit.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SystemPromptRules.LengthCeiling + 1)]
    public void LocalModel_LimitOutOfRange_IsInvalid(int limit)
    {
        var problems = Problems(new LlamaCppOptions { RequestFields = ["messages"], MaxSystemPromptLength = limit });

        Assert.Contains(problems, problem => problem.MemberNames.Contains(nameof(LlamaCppOptions.MaxSystemPromptLength)));
    }

    /// <summary>The local model accepts a configured prompt at the limit, and refuses a longer prompt.</summary>
    [Fact]
    public void LocalModel_ConfiguredPromptLength_IsChecked()
    {
        Assert.Empty(Problems(new LlamaCppOptions { RequestFields = ["messages"], MaxSystemPromptLength = 3, SystemPrompt = "123" }));

        var problems = Problems(new LlamaCppOptions { RequestFields = ["messages"], MaxSystemPromptLength = 3, SystemPrompt = "1234" });

        Assert.Contains(problems, problem => problem.ErrorMessage!.Contains("SystemPrompt", StringComparison.Ordinal));
    }

    /// <summary>A disabled local model still has its limit checked, like its other settings.</summary>
    [Fact]
    public void LocalModel_DisabledWithBadLimit_FailsValidation()
    {
        using var services = Build(new()
        {
            ["LocalModel:Enabled"] = "false",
            ["LocalModel:RequestFields:0"] = "messages",
            ["LocalModel:MaxSystemPromptLength"] = "0",
        });

        var failure = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<LlamaCppOptions>>().Value);

        Assert.Contains("MaxSystemPromptLength", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A cloud provider accepts the highest limit.</summary>
    [Fact]
    public void Provider_LimitAtCeiling_IsValid()
    {
        Assert.Empty(Problems(new ProviderOptions { BaseUrl = new Uri("https://api.example.com"), RequestFields = ["messages"], MaxSystemPromptLength = SystemPromptRules.LengthCeiling }));
    }

    /// <summary>A cloud provider with no API key still has its limit checked, so that a mistake appears before someone adds the key.</summary>
    [Fact]
    public void Provider_NoKeyWithBadLimit_StopsStartup()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["LocalModel:Enabled"] = "false",
            ["LocalModel:RequestFields:0"] = "messages",
            ["Providers:cloud:BaseUrl"] = "https://api.example.com",
            ["Providers:cloud:RequestFields:0"] = "messages",
            ["Providers:cloud:MaxSystemPromptLength"] = "1000001",
        }));

        Assert.Contains("MaxSystemPromptLength", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The configuration reaches an enabled cloud provider, which gives the prompt and the limit to the engine.</summary>
    [Fact]
    public void Provider_ConfiguredValues_ReachProvider()
    {
        using var services = Build(new()
        {
            ["LocalModel:Enabled"] = "false",
            ["LocalModel:RequestFields:0"] = "messages",
            ["Providers:cloud:BaseUrl"] = "https://api.example.com",
            ["Providers:cloud:ApiKey"] = "secret",
            ["Providers:cloud:RequestFields:0"] = "messages",
            ["Providers:cloud:SystemPrompt"] = "Be brief.",
            ["Providers:cloud:MaxSystemPromptLength"] = "123",
        });

        var provider = services.GetServices<IChatProvider>().Single();

        Assert.Equal("Be brief.", provider.InitialSystemPrompt);
        Assert.Equal(123, provider.MaxSystemPromptLength);
    }

    /// <summary>A cloud provider with no limit in its configuration uses the default.</summary>
    [Fact]
    public void Provider_NoConfiguredLimit_UsesDefault()
    {
        using var services = Build(new()
        {
            ["LocalModel:Enabled"] = "false",
            ["LocalModel:RequestFields:0"] = "messages",
            ["Providers:cloud:BaseUrl"] = "https://api.example.com",
            ["Providers:cloud:ApiKey"] = "secret",
            ["Providers:cloud:RequestFields:0"] = "messages",
        });

        Assert.Equal(SystemPromptRules.DefaultLength, services.GetServices<IChatProvider>().Single().MaxSystemPromptLength);
    }

    /// <summary>Runs the same validation as the engine's startup.</summary>
    /// <param name="options">The settings.</param>
    /// <returns>The problems.</returns>
    private static List<ValidationResult> Problems(object options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    /// <summary>Registers the Infrastructure layer with the given configuration.</summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The services.</returns>
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddInfrastructure(configuration).BuildServiceProvider();
    }
}

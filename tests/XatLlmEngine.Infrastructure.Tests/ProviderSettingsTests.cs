using XatLlmEngine.Infrastructure.Providers;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the rules for cloud provider settings.</summary>
public class ProviderSettingsTests
{
    /// <summary>The settings accept secure addresses, and plain addresses on this machine.</summary>
    /// <param name="address">The base address.</param>
    [Theory]
    [InlineData("https://api.example.com/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://localhost:11434/v1")]
    public void Validate_SafeAddress_Passes(string address)
    {
        DependencyInjection.Validate("test", Options(address));
    }

    /// <summary>Addresses that would expose the API key, or that are not web addresses at all, stop the engine at startup.</summary>
    /// <param name="address">The base address.</param>
    [Theory]
    [InlineData("http://api.example.com/v1")]
    [InlineData("file:///etc/passwd")]
    public void Validate_UnsafeAddress_Throws(string address)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DependencyInjection.Validate("test", Options(address)));
    }

    /// <summary>A provider cannot use the reserved name. A provider also cannot use a name with a slash, because no request can select it.</summary>
    /// <param name="name">The provider's name.</param>
    [Theory]
    [InlineData("local")]
    [InlineData("LOCAL")]
    [InlineData("a/b")]
    public void Validate_InvalidName_Throws(string name)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DependencyInjection.Validate(name, Options("https://api.example.com")));
    }

    /// <summary>A provider with no request template could accept nothing, so the mistake stops the engine at startup.</summary>
    [Fact]
    public void Validate_NoRequestFields_Throws()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            DependencyInjection.Validate("test", new ProviderOptions { BaseUrl = new Uri("https://api.example.com") }));

        Assert.Contains("RequestFields", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Builds valid settings for one address.</summary>
    /// <param name="address">The base address.</param>
    /// <returns>The settings.</returns>
    private static ProviderOptions Options(string address) => new() { BaseUrl = new Uri(address), RequestFields = ["messages"] };
}

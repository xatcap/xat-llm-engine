using Microsoft.Extensions.Configuration;
using XatLlmEngine.Host.Security;

namespace XatLlmEngine.Host.Tests;

/// <summary>Tests the rule that an unprotected API stays on loopback.</summary>
public class ListenAddressPolicyTests
{
    /// <summary>Loopback addresses need no key.</summary>
    /// <param name="key">The setting that holds the address.</param>
    /// <param name="value">The address.</param>
    [Theory]
    [InlineData("urls", "http://127.0.0.1:8420")]
    [InlineData("urls", "http://localhost:8420;http://[::1]:8420")]
    [InlineData("Kestrel:Endpoints:Main:Url", "http://127.0.0.1:8420")]
    public void EnsureAllowed_LoopbackWithoutKeys_Passes(string key, string value)
    {
        ListenAddressPolicy.EnsureAllowed(Configuration((key, value)));
    }

    /// <summary>Every way of opening the network without a key stops the engine.</summary>
    /// <param name="key">The setting that holds the address.</param>
    /// <param name="value">The address.</param>
    [Theory]
    [InlineData("urls", "http://0.0.0.0:8420")]
    [InlineData("urls", "http://*:8420")]
    [InlineData("urls", "http://+:8420")]
    [InlineData("urls", "http://[::]:8420")]
    [InlineData("urls", "http://192.168.1.5:8420")]
    [InlineData("urls", "http://127.0.0.1:8420;http://0.0.0.0:8421")]
    [InlineData("http_ports", "8420")]
    [InlineData("https_ports", "8421")]
    [InlineData("Kestrel:Endpoints:Main:Url", "http://0.0.0.0:8420")]
    public void EnsureAllowed_NetworkWithoutKeys_Throws(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => ListenAddressPolicy.EnsureAllowed(Configuration((key, value))));
    }

    /// <summary>A blank key gives no protection, so the network stays closed.</summary>
    [Fact]
    public void EnsureAllowed_NetworkWithBlankKey_Throws()
    {
        var configuration = Configuration(("urls", "http://0.0.0.0:8420"), ("Engine:ApiKeys:0", " "));

        Assert.Throws<InvalidOperationException>(() => ListenAddressPolicy.EnsureAllowed(configuration));
    }

    /// <summary>With a key, the policy allows the network.</summary>
    [Fact]
    public void EnsureAllowed_NetworkWithKey_Passes()
    {
        ListenAddressPolicy.EnsureAllowed(Configuration(("urls", "http://0.0.0.0:8420"), ("Engine:ApiKeys:0", "secret")));
    }

    /// <summary>Builds configuration from values.</summary>
    /// <param name="values">The settings.</param>
    /// <returns>The configuration.</returns>
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
}

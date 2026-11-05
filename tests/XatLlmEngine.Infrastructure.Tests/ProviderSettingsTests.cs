using System.Net;
using System.Net.Http.Headers;
using XatLlmEngine.Infrastructure.Providers;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the rules for cloud provider settings and retries.</summary>
public class ProviderSettingsTests
{
    /// <summary>Secure addresses, and plain addresses on this machine, are accepted.</summary>
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

    /// <summary>The reserved name cannot be taken, and a name with a slash could never be selected.</summary>
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

    /// <summary>A provider cannot hold a caller's request longer than the limit by asking for a long wait.</summary>
    [Fact]
    public void RetryDelay_LongRetryAfter_IsCapped()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromSeconds(30), DependencyInjection.RetryDelay(response, DateTimeOffset.UnixEpoch));
    }

    /// <summary>A short wait is followed as asked.</summary>
    [Fact]
    public void RetryDelay_ShortRetryAfter_IsFollowed()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), DependencyInjection.RetryDelay(response, DateTimeOffset.UnixEpoch));
    }

    /// <summary>A header with a date in the past means no wait.</summary>
    [Fact]
    public void RetryDelay_DateInPast_IsZero()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UnixEpoch);

        Assert.Equal(TimeSpan.Zero, DependencyInjection.RetryDelay(response, DateTimeOffset.UnixEpoch.AddDays(1)));
    }

    /// <summary>Without the header, the normal backoff applies.</summary>
    [Fact]
    public void RetryDelay_NoHeader_UsesBackoff()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        Assert.Null(DependencyInjection.RetryDelay(response, DateTimeOffset.UnixEpoch));
        Assert.Null(DependencyInjection.RetryDelay(null, DateTimeOffset.UnixEpoch));
    }

    /// <summary>Builds valid settings for one address.</summary>
    /// <param name="address">The base address.</param>
    /// <returns>The settings.</returns>
    private static ProviderOptions Options(string address) => new() { BaseUrl = new Uri(address), RequestFields = ["messages"] };
}

using System.Net;
using System.Net.Http.Headers;
using XatLlmEngine.Infrastructure.Providers;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the rules for the wait that a provider asks for in its Retry-After header.</summary>
public class HttpFailureTests
{
    /// <summary>A provider cannot hold a caller's request longer than the limit by asking for a long wait.</summary>
    [Fact]
    public void RetryDelay_LongRetryAfter_IsCapped()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromSeconds(30), HttpFailure.RetryDelay(response, DateTimeOffset.UnixEpoch));
    }

    /// <summary>The engine obeys a short wait, as the provider asked.</summary>
    [Fact]
    public void RetryDelay_ShortRetryAfter_IsFollowed()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), HttpFailure.RetryDelay(response, DateTimeOffset.UnixEpoch));
    }

    /// <summary>A header with a date in the past means no wait.</summary>
    [Fact]
    public void RetryDelay_DateInPast_IsZero()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UnixEpoch);

        Assert.Equal(TimeSpan.Zero, HttpFailure.RetryDelay(response, DateTimeOffset.UnixEpoch.AddDays(1)));
    }

    /// <summary>Without the header, the normal backoff applies.</summary>
    [Fact]
    public void RetryDelay_NoHeader_UsesBackoff()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        Assert.Null(HttpFailure.RetryDelay(response, DateTimeOffset.UnixEpoch));
        Assert.Null(HttpFailure.RetryDelay(null, DateTimeOffset.UnixEpoch));
    }
}

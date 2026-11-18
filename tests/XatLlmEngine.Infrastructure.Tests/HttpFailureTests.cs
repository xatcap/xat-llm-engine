using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XatLlmEngine.Domain;
using XatLlmEngine.Infrastructure.Providers;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the rules for failed responses of llama-server.</summary>
public class HttpFailureTests
{
    /// <summary>The engine forwards the message of the server for a problem that the caller can correct.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="kind">The expected error kind.</param>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, ErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.NotFound, ErrorKind.NotFound)]
    public void FromResponse_CorrectableStatus_ForwardsServerMessage(HttpStatusCode status, ErrorKind kind)
    {
        using var response = new HttpResponseMessage(status);

        var error = HttpFailure.FromResponse("test", response, """{"error":{"message":"custom-problem"}}""", NullLogger.Instance);

        Assert.Equal(kind, error.Kind);
        Assert.Equal("custom-problem", error.Message);
    }

    /// <summary>A body that has no readable message gives the general message for the status.</summary>
    /// <param name="body">The response body.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"error":{"message":"   "}}""")]
    public void FromResponse_NoReadableMessage_UsesGeneralMessage(string? body)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest);

        var error = HttpFailure.FromResponse("test", response, body, NullLogger.Instance);

        Assert.Equal("The provider 'test' rejected the request.", error.Message);
    }

    /// <summary>The engine does not forward the message of a server fault, and it does not tell the user to correct a key.</summary>
    [Fact]
    public void FromResponse_Unauthorized_HidesBodyAndGivesLocalAdvice()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var error = HttpFailure.FromResponse("test", response, """{"error":{"message":"sk-secret"}}""", NullLogger.Instance);

        Assert.Equal(ErrorKind.Upstream, error.Kind);
        Assert.DoesNotContain("sk-secret", error.Message, StringComparison.Ordinal);
        Assert.Contains("Restart the engine", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A busy server is a temporary failure. Any other status is a fault of the server.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="kind">The expected error kind.</param>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErrorKind.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorKind.Upstream)]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorKind.Upstream)]
    public void FromResponse_ServerStatus_MapsToKind(HttpStatusCode status, ErrorKind kind)
    {
        using var response = new HttpResponseMessage(status);

        var error = HttpFailure.FromResponse("test", response, """{"error":{"message":"hidden"}}""", NullLogger.Instance);

        Assert.Equal(kind, error.Kind);
        Assert.DoesNotContain("hidden", error.Message, StringComparison.Ordinal);
    }
}

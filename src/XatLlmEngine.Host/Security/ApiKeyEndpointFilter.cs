using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XatLlmEngine.Domain;
using XatLlmEngine.Host.Contracts;

namespace XatLlmEngine.Host.Security;

/// <summary>Requires a valid <c>Authorization: Bearer</c> key on the API, when the configuration has keys.</summary>
/// <remarks>
/// This filter compares keys as SHA-256 hashes in constant time, so response timing does not show their content or their
/// length. When the configuration has no keys, <see cref="ListenAddressPolicy"/> guarantees that the API is on loopback only.
/// </remarks>
internal sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    /// <summary>The text before the key in the header.</summary>
    private const string BearerPrefix = "Bearer ";

    /// <summary>The hashes of the accepted keys.</summary>
    private readonly IReadOnlyList<byte[]> keyHashes;

    /// <summary>Initializes a new instance of the <see cref="ApiKeyEndpointFilter"/> class.</summary>
    /// <param name="options">The engine's settings.</param>
    public ApiKeyEndpointFilter(IOptions<EngineOptions> options)
    {
        keyHashes = [.. options.Value.EffectiveKeys.Select(Hash)];
    }

    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (keyHashes.Count == 0 || IsAuthorized(context.HttpContext.Request.Headers.Authorization.ToString()))
        {
            return await next(context).ConfigureAwait(false);
        }

        return ErrorMapping.Unauthorized(context.HttpContext);
    }

    /// <summary>Hashes a key.</summary>
    /// <param name="key">The key text.</param>
    /// <returns>The SHA-256 hash.</returns>
    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Checks the header against every key, without stopping early.</summary>
    /// <param name="header">The header value.</param>
    /// <returns><see langword="true"/> when the header holds an accepted key.</returns>
    private bool IsAuthorized(string header)
    {
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = Hash(header[BearerPrefix.Length..].Trim());
        var authorized = false;
        foreach (var keyHash in keyHashes)
        {
            authorized |= CryptographicOperations.FixedTimeEquals(candidate, keyHash);
        }

        return authorized;
    }
}

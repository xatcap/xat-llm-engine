using System.Net;
using Microsoft.AspNetCore.Http;

namespace XatLlmEngine.Host.Security;

/// <summary>Stops the engine from serving an unprotected API on the network.</summary>
internal static class ListenAddressPolicy
{
    /// <summary>Refuses to start on a non-loopback address, because the API has no authentication. Without this rule, anyone on the network can spend the credit of the cloud accounts.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <exception cref="InvalidOperationException">The API would be open to the network.</exception>
    public static void EnsureAllowed(IConfiguration configuration)
    {
        var exposed = ConfiguredAddresses(configuration).FirstOrDefault(address => !IsLoopback(address));
        if (exposed is not null)
        {
            throw new InvalidOperationException(
                $"The configuration makes the engine listen on {exposed}. The API has no authentication, so listen on 127.0.0.1 only.");
        }
    }

    /// <summary>Tells whether an address is reachable only from this machine.</summary>
    /// <param name="address">A listening address, for example <c>http://127.0.0.1:8420</c>.</param>
    /// <returns><see langword="true"/> for loopback addresses and Unix sockets.</returns>
    internal static bool IsLoopback(string address)
    {
        var binding = BindingAddress.Parse(address);
        return binding.IsUnixPipe
            || string.Equals(binding.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(binding.Host, out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>Collects the addresses from every setting that can make the server listen.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The addresses.</returns>
    private static IEnumerable<string> ConfiguredAddresses(IConfiguration configuration)
    {
        foreach (var address in Split(configuration["urls"]))
        {
            yield return address;
        }

        foreach (var (key, scheme) in new[] { ("http_ports", "http"), ("https_ports", "https") })
        {
            foreach (var port in Split(configuration[key]))
            {
                yield return $"{scheme}://*:{port}";
            }
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (endpoint["Url"] is { Length: > 0 } url)
            {
                yield return url;
            }
        }
    }

    /// <summary>Splits a semicolon-separated setting.</summary>
    /// <param name="value">The setting's value.</param>
    /// <returns>The non-empty parts.</returns>
    private static string[] Split(string? value) =>
        value?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}

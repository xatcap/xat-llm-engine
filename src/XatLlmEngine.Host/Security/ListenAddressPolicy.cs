using System.Net;
using Microsoft.AspNetCore.Http;

namespace XatLlmEngine.Host.Security;

/// <summary>Stops the engine from serving an unprotected API on the network.</summary>
internal static class ListenAddressPolicy
{
    /// <summary>
    /// Refuses to start on a non-loopback address, because the API has no authentication. Without this rule, anyone on the network can spend the credit of the cloud accounts.
    /// Also refuses port 0 on <c>localhost</c>, which the server cannot use.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <exception cref="InvalidOperationException">The API would be open to the network, or the address cannot work.</exception>
    public static void EnsureAllowed(IConfiguration configuration)
    {
        var addresses = ConfiguredAddresses(configuration).ToList();
        var exposed = addresses.FirstOrDefault(address => !IsLoopback(address));
        if (exposed is not null)
        {
            throw new InvalidOperationException(
                $"The configuration makes the engine listen on {exposed}. The API has no authentication, so listen on 127.0.0.1 only.");
        }

        var dynamicLocalhost = addresses.FirstOrDefault(IsDynamicLocalhost);
        if (dynamicLocalhost is not null)
        {
            throw new InvalidOperationException(
                $"The configuration makes the engine listen on {dynamicLocalhost}. The server cannot use port 0 with 'localhost'. Use http://127.0.0.1:0 instead.");
        }
    }

    /// <summary>Tells whether an address asks for a port that the system chooses on the name <c>localhost</c>.</summary>
    /// <param name="address">A listening address.</param>
    /// <returns><see langword="true"/> for <c>localhost</c> with port 0.</returns>
    internal static bool IsDynamicLocalhost(string address)
    {
        var binding = BindingAddress.Parse(address);
        return !binding.IsUnixPipe && binding.Port == 0 && string.Equals(binding.Host, "localhost", StringComparison.OrdinalIgnoreCase);
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

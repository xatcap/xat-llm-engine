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
    /// <exception cref="InvalidOperationException">The API would be open to the network, or an address has the wrong format or cannot work.</exception>
    public static void EnsureAllowed(IConfiguration configuration)
    {
        var addresses = ConfiguredAddresses(configuration).Select(address => (Text: address, Binding: Parse(address))).ToList();
        var exposed = addresses.FirstOrDefault(address => !IsLoopback(address.Binding));
        if (exposed.Text is not null)
        {
            throw new InvalidOperationException(
                $"The configuration makes the engine listen on {exposed.Text}. The API has no authentication, so listen on 127.0.0.1 only.");
        }

        var dynamicLocalhost = addresses.FirstOrDefault(address => IsDynamicLocalhost(address.Binding));
        if (dynamicLocalhost.Text is not null)
        {
            throw new InvalidOperationException(
                $"The configuration makes the engine listen on {dynamicLocalhost.Text}. The server cannot use port 0 with 'localhost'. Use http://127.0.0.1:0 instead.");
        }
    }

    /// <summary>Reads a listening address. An address with the wrong format stops the engine with a message that names the address.</summary>
    /// <param name="address">A listening address.</param>
    /// <returns>The parts of the address.</returns>
    /// <exception cref="InvalidOperationException">The address has the wrong format.</exception>
    private static BindingAddress Parse(string address)
    {
        try
        {
            return BindingAddress.Parse(address);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"The engine cannot read the listening address '{address}'. {exception.Message}", exception);
        }
    }

    /// <summary>Tells whether an address asks for a port that the system chooses on the name <c>localhost</c>.</summary>
    /// <param name="binding">The parts of a listening address.</param>
    /// <returns><see langword="true"/> for <c>localhost</c> with port 0.</returns>
    private static bool IsDynamicLocalhost(BindingAddress binding) =>
        !binding.IsUnixPipe && binding.Port == 0 && string.Equals(binding.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tells whether an address is reachable only from this machine.</summary>
    /// <param name="binding">The parts of a listening address, for example <c>http://127.0.0.1:8420</c>.</param>
    /// <returns><see langword="true"/> for loopback addresses and Unix sockets.</returns>
    private static bool IsLoopback(BindingAddress binding)
    {
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

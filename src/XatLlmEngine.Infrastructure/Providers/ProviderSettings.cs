using Microsoft.Extensions.Configuration;
using XatLlmEngine.Infrastructure.LlamaCpp;

namespace XatLlmEngine.Infrastructure.Providers;

/// <summary>Reads and checks the settings of every cloud provider. Every entry is checked, also an entry with no API key.</summary>
internal static class ProviderSettings
{
    /// <summary>The request fields that set the output token limit. They are the same as in the Application rule.</summary>
    internal static readonly string[] TokenLimitFields = ["max_tokens", "max_completion_tokens"];

    /// <summary>Reads all entries under <see cref="ProviderOptions.SectionName"/>.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>Each provider's name and settings, in configuration order.</returns>
    /// <exception cref="InvalidOperationException">The settings of a provider are invalid.</exception>
    public static IReadOnlyList<(string Name, ProviderOptions Options)> Read(IConfiguration configuration)
    {
        var entries = new List<(string, ProviderOptions)>();
        foreach (var section in configuration.GetSection(ProviderOptions.SectionName).GetChildren())
        {
            var problems = new List<string>();
            ValidateType(section[nameof(ProviderOptions.Type)], problems);
            var options = SettingsRules.Bind<ProviderOptions>(section, problems);
            if (problems.Count == 0)
            {
                problems.AddRange(Validate(section.Key, options));
            }

            SettingsRules.ThrowIfAny($"provider '{section.Key}'", problems);
            entries.Add((section.Key, options));
        }

        return entries;
    }

    /// <summary>Checks the rules of one provider's bound settings.</summary>
    /// <param name="name">The provider's name.</param>
    /// <param name="options">The provider's settings.</param>
    /// <returns>Each problem. The list is empty when the settings are valid.</returns>
    internal static List<string> Validate(string name, ProviderOptions options)
    {
        var problems = new List<string>();
        if (string.Equals(name, LocalChatProvider.ProviderName, StringComparison.OrdinalIgnoreCase) || name.Contains('/', StringComparison.Ordinal))
        {
            problems.Add($"A provider cannot use the name '{name}'. The engine reserves the name '{LocalChatProvider.ProviderName}', and names cannot contain '/'.");
        }

        SettingsRules.Validate(options, problems);
        if (options.Type is { } type && !Enum.IsDefined(type))
        {
            problems.Add($"The Type '{(int)type}' does not exist. Use one of: {string.Join(", ", Enum.GetNames<ProviderType>())}.");
        }

        if (options.BaseUrl is { IsAbsoluteUri: false })
        {
            problems.Add("The BaseUrl must be an absolute address.");
        }
        else if (options.BaseUrl is { } address && !IsSafeAddress(address))
        {
            problems.Add("The BaseUrl must use https, or http on this machine only.");
        }

        if (options.MaxTokensLimit > 0 && !TokenLimitFields.Any(field => options.RequestFields.Contains(field, StringComparer.Ordinal)))
        {
            problems.Add($"MaxTokensLimit needs one of these fields in RequestFields: {string.Join(", ", TokenLimitFields)}.");
        }

        // A key read from a file often ends with a line break. The HTTP headers would refuse it on every request.
        if (!string.IsNullOrWhiteSpace(options.ApiKey) && options.ApiKey.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            problems.Add("The ApiKey contains spaces, line breaks or control characters. Remove them.");
        }

        return problems;
    }

    /// <summary>Tells whether an address keeps the API key safe. Every request includes the key, so the engine allows plain HTTP only on this machine.</summary>
    /// <param name="address">An absolute address.</param>
    /// <returns><see langword="true"/> for https, and for http to a loopback address.</returns>
    private static bool IsSafeAddress(Uri address) =>
        address.Scheme == Uri.UriSchemeHttps || (address.Scheme == Uri.UriSchemeHttp && address.IsLoopback);

    /// <summary>Checks the raw Type text. The binder accepts a number, such as "5", even when no protocol has that number.</summary>
    /// <param name="value">The configured text, or <see langword="null"/> when the entry has no Type.</param>
    /// <param name="problems">Receives each problem.</param>
    private static void ValidateType(string? value, List<string> problems)
    {
        var names = string.Join(", ", Enum.GetNames<ProviderType>());
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"The Type is missing. Set it to one of: {names}.");
        }
        else if (!Enum.GetNames<ProviderType>().Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"The Type '{value}' does not exist. Use one of: {names}.");
        }
    }
}

using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace XatLlmEngine.Infrastructure;

/// <summary>Reads and checks the engine's own settings sections, so that a mistake stops the engine at startup rather than failing on a request.</summary>
internal static class SettingsRules
{
    /// <summary>Binds a section to a new settings object. An unknown key, or a value that cannot convert, is a problem.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <param name="section">The configuration section.</param>
    /// <param name="problems">Receives each problem. The binding continues after an unknown key, so that all problems show at once.</param>
    /// <returns>The settings. Properties with problems keep their default values.</returns>
    public static T Bind<T>(IConfigurationSection section, List<string> problems)
        where T : new()
    {
        var known = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A removed setting, such as MaxRetries, or a wrong name, such as ApiKy, would otherwise have no effect and no message.
        foreach (var child in section.GetChildren().Where(child => !known.Contains(child.Key)))
        {
            problems.Add($"The setting '{child.Key}' does not exist. Remove it, or correct its name.");
        }

        var settings = new T();
        try
        {
            section.Bind(settings);
        }
        catch (InvalidOperationException failure)
        {
            problems.Add($"A value has the wrong format: {failure.Message}");
        }

        return settings;
    }

    /// <summary>Runs the data annotations and the <see cref="IValidatableObject"/> rules of a settings object.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="problems">Receives each problem.</param>
    public static void Validate(object settings, List<string> problems)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(settings, new ValidationContext(settings), results, validateAllProperties: true))
        {
            problems.AddRange(results.Select(result => result.ErrorMessage ?? "A value is not valid."));
        }
    }

    /// <summary>Stops the engine when the list has problems.</summary>
    /// <param name="subject">The settings that the problems apply to, for example "provider 'openai'".</param>
    /// <param name="problems">The problems.</param>
    /// <exception cref="InvalidOperationException">The list has at least one problem.</exception>
    public static void ThrowIfAny(string subject, IReadOnlyCollection<string> problems)
    {
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"The settings of {subject} are invalid: {string.Join(' ', problems)}");
        }
    }
}
